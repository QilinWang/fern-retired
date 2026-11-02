using System.CommandLine;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fern.Run.Cli;

// Ported from the Go fern-card (tools/cardbox-go/fern/corrections.go and its
// dispatch in cmd/fern-card/main.go), which is retiring. The package comment
// that owned the rule moves with the code:
//
//   Package corrections projects Fern's append-only Corrections source into the
//   bounded digest every agent session loads. Projection is pure; Refresh owns
//   the one durable local-file mutation that commits its result.
//
// The generated header still names `fern-card refresh-corrections` as the
// regenerating command because that string is inside the digest bytes, and the
// digest is pinned byte for byte against what Go wrote. Changing it is a
// deliberate content change, not part of the port.

/// <summary>Corrections digest projection: `fern-run check-corrections` and `fern-run refresh-corrections`.</summary>
public static class CorrectionsDigestVerbs
{
    const string SourcePath = ".ai/corrections.md";
    const string DigestPath = ".ai/corrections_active.md";
    const string AppendixHeading = "## Active Digest Blocks";
    const string BlockOpen = "[digest]";
    const string BlockClose = "[/digest]";
    const int BlockCharacterLimit = 1000;
    const int LinesThatRequireABlock = 12;
    const int ActiveDigestLineBudget = 2000;

    const string StaleDigestRefusal =
        "generated .ai/corrections_active.md is missing or stale; run fern-run refresh-corrections";

    static readonly string[] BlockFields = ["Rule", "Action", "Exception", "Evidence"];
    static readonly UTF8Encoding NoBomUtf8 = new(false);

    /// <summary>One refused projection or check; the message is the Go error text, unchanged.</summary>
    internal sealed class CorrectionsRefusal(string message) : Exception(message);

    /// <summary>The deterministic result of projecting one Corrections source.</summary>
    internal sealed record Projection(string Markdown, int HistoryEntries);

    /// <summary>The public receipt for the only Corrections mutation.</summary>
    // CHG 2026-09-26 (owner: "get rid of 256 entirely"): digest_sha256, a SHA-256 of the
    // projected bytes nothing read, is gone; the schema moves to v2 with the shape.
    internal sealed record RefreshReceipt(
        [property: JsonPropertyName("schema")] string Schema,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("source_path")] string SourcePath,
        [property: JsonPropertyName("digest_path")] string DigestPath,
        [property: JsonPropertyName("source_lines")] int SourceLines,
        [property: JsonPropertyName("digest_lines")] int DigestLines,
        [property: JsonPropertyName("history_entries")] int HistoryEntries);

    internal sealed record InventoryEntry(
        [property: JsonPropertyName("canonical_path")] string CanonicalPath,
        [property: JsonPropertyName("classification")] string Classification,
        [property: JsonPropertyName("diagnostics")] IReadOnlyList<string> Diagnostics);

    // CHG 2026-09-26: source_tree_digest, a SHA-256 over the Corrections source nothing read,
    // is gone; the check itself compares the digest file's bytes with the projection.
    internal sealed record InventoryReceipt(
        [property: JsonPropertyName("schema")] string Schema,
        [property: JsonPropertyName("entries")] IReadOnlyList<InventoryEntry> Entries);

    sealed class EntryBlock
    {
        public string Id { get; init; } = "";

        public List<string> Lines { get; } = [];
    }

    public static void Add(Command parent)
    {
        parent.Subcommands.Add(CheckCorrections());
        parent.Subcommands.Add(RefreshCorrections());
    }

    static Command RefreshCorrections()
    {
        var command = new Command(
            "refresh-corrections",
            "Regenerate .ai/corrections_active.md from .ai/corrections.md; the only Corrections mutation.");
        var root = new Option<string>("--root")
        {
            Description = "repository root whose Corrections digest is refreshed",
            DefaultValueFactory = _ => ".",
        };
        command.Options.Add(root);
        command.SetAction(parsed =>
        {
            try
            {
                WriteJson(Refresh(parsed.GetValue(root)!));
                return 0;
            }
            catch (Exception error) when (error is CorrectionsRefusal or IOException or UnauthorizedAccessException)
            {
                return Fail(error.Message);
            }
        });
        return command;
    }

    static Command CheckCorrections()
    {
        var command = new Command(
            "check-corrections",
            "Refuse when .ai/corrections_active.md is missing or is not the projection of .ai/corrections.md.");
        var root = new Option<string>("--root")
        {
            Description = "repository root whose Corrections source and digest are checked",
            DefaultValueFactory = _ => ".",
        };
        command.Options.Add(root);
        command.SetAction(parsed =>
        {
            string canonicalRoot;
            try
            {
                canonicalRoot = Path.GetFullPath(parsed.GetValue(root)!);
                // CHG 2026-09-26: the bytes fed the removed source-tree hash; the read stays
                // because an absent or unreadable source must refuse here with its error.
                _ = File.ReadAllBytes(JoinPath(canonicalRoot, SourcePath));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return Fail(error.Message);
            }

            string? refusal = null;
            try
            {
                CheckDigest(canonicalRoot);
            }
            catch (Exception error) when (error is CorrectionsRefusal or IOException or UnauthorizedAccessException)
            {
                refusal = error.Message;
            }

            var diagnostics = new List<string>();
            if (refusal is not null)
            {
                diagnostics.Add(refusal);
            }
            WriteJson(new InventoryReceipt(
                "fern.knowledge.inventory.v2",
                [new InventoryEntry(SourcePath, refusal is null ? "typed" : "invalid", diagnostics)]));
            return refusal is null ? 0 : Fail("Corrections check refused: " + refusal);
        });
        return command;
    }

    /// <summary>Returns the exact bounded digest for source without writing files.</summary>
    internal static Projection Project(string source)
    {
        var allLines = SourceLines(source);
        var (lines, appendix) = SplitOffAppendix(allLines);
        var digestBlocks = ParseDigestBlocks(appendix);

        var entryLineNumbers = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < lines.Count; index++)
        {
            if (CorrectionEntryId(lines[index]) is { } id)
            {
                entryLineNumbers[id] = index + 1;
            }
        }

        var unknownIds = digestBlocks.Keys.Where(id => !entryLineNumbers.ContainsKey(id)).ToList();
        if (unknownIds.Count > 0)
        {
            unknownIds.Sort(StringComparer.Ordinal);
            throw new CorrectionsRefusal(
                $"digest block(s) name correction(s) {QuotedStrings(unknownIds)} that no entry declares; "
                + "a summary of nothing cannot be projected");
        }

        var blocks = SplitEntries(lines);
        RefuseDigestBlockForShortEntry(blocks, digestBlocks);
        var retiredIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var block in blocks)
        {
            foreach (var line in block.Lines)
            {
                if (StatusTag(line) && block.Id.Length > 0)
                {
                    retiredIds.Add(block.Id);
                }
            }
        }

        var output = new List<string>();
        foreach (var block in blocks)
        {
            if (block.Id.Length > 0 && retiredIds.Contains(block.Id))
            {
                continue;
            }

            if (block.Id.Length > 0 && digestBlocks.TryGetValue(block.Id, out var digest))
            {
                output.AddRange(ProjectBlock(block.Id, digest, entryLineNumbers[block.Id]));
                for (var index = block.Lines.Count - 1; index >= 0 && block.Lines[index].Trim().Length == 0; index--)
                {
                    output.Add("");
                }
                continue;
            }

            foreach (var line in block.Lines)
            {
                if (CorrectionIndexLine(line) is { } indexLine)
                {
                    var surviving = indexLine.Numbers.Where(number => !retiredIds.Contains(number)).ToList();
                    if (surviving.Count > 0)
                    {
                        output.Add("- " + indexLine.Label + ": " + string.Join(", ", surviving));
                    }
                }
                else
                {
                    output.Add(line);
                }
            }
        }

        RefuseSecondThemeIndex(ThemeIndexRegion(output));
        RefuseUnsummarisedLongEntry(blocks, retiredIds, digestBlocks);
        RefuseDigestOverBudget(output);
        while (output.Count > 0 && output[^1].Trim().Length == 0)
        {
            output.RemoveAt(output.Count - 1);
        }

        var joined = string.Join("\n", output);
        const string firstHeading = "# Corrections\n";
        var headingAt = joined.IndexOf(firstHeading, StringComparison.Ordinal);
        var body = headingAt < 0 ? joined : joined.Remove(headingAt, firstHeading.Length);
        return new Projection(
            "# Corrections — active digest (GENERATED, do not edit)\n"
            + "# Source: .ai/corrections.md. Regenerate: fern-run refresh-corrections\n"
            + $"# {retiredIds.Count} entries are history (superseded, enforced by the control\n"
            + "# plane, or dormant) and live only in the source file; nothing here was reworded.\n"
            + body + "\n",
            retiredIds.Count);
    }

    /// <summary>
    /// Atomically replaces the digest only after projection succeeds, then
    /// reopens the committed path before issuing its receipt.
    /// </summary>
    internal static RefreshReceipt Refresh(string root)
    {
        var sourceFile = JoinPath(root, SourcePath);
        var digestFile = JoinPath(root, DigestPath);
        byte[] sourceBytes;
        try
        {
            sourceBytes = File.ReadAllBytes(sourceFile);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new CorrectionsRefusal($"could not read Corrections source {sourceFile}: {error.Message}");
        }

        var projected = Project(NoBomUtf8.GetString(sourceBytes));
        var projectedBytes = NoBomUtf8.GetBytes(projected.Markdown);
        var status = "unchanged";
        if (!File.Exists(digestFile) || !File.ReadAllBytes(digestFile).AsSpan().SequenceEqual(projectedBytes))
        {
            DurableReplace(digestFile, projectedBytes);
            if (!File.ReadAllBytes(digestFile).AsSpan().SequenceEqual(projectedBytes))
            {
                throw new CorrectionsRefusal(
                    $"reopened Corrections digest {digestFile} differs from the bytes that were committed");
            }
            status = "written";
        }

        return new RefreshReceipt(
            "fern.knowledge.corrections-digest-refresh.v2",
            status,
            SourcePath,
            DigestPath,
            SourceLines(NoBomUtf8.GetString(sourceBytes)).Count,
            SourceLines(projected.Markdown).Count,
            projected.HistoryEntries);
    }

    /// <summary>Refuses when the generated digest is absent or not the projection.</summary>
    internal static void CheckDigest(string root)
    {
        var sourceFile = JoinPath(root, SourcePath);
        if (!File.Exists(sourceFile))
        {
            return;
        }

        var expected = NoBomUtf8.GetBytes(Project(NoBomUtf8.GetString(File.ReadAllBytes(sourceFile))).Markdown);
        var digestFile = JoinPath(root, DigestPath);
        if (File.Exists(digestFile) && File.ReadAllBytes(digestFile).AsSpan().SequenceEqual(expected))
        {
            return;
        }

        throw new CorrectionsRefusal(StaleDigestRefusal);
    }

    static void DurableReplace(string path, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = path + ".partial." + Environment.ProcessId;
        File.WriteAllBytes(temporary, bytes);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                temporary,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Go's line split: trailing newline is a terminator, not an empty last line.</summary>
    static List<string> SourceLines(string source)
    {
        if (source.Length == 0)
        {
            return [];
        }

        var lines = source.Split('\n').ToList();
        if (source.EndsWith('\n'))
        {
            lines.RemoveAt(lines.Count - 1);
        }
        for (var index = 0; index < lines.Count; index++)
        {
            if (lines[index].EndsWith('\r'))
            {
                lines[index] = lines[index][..^1];
            }
        }
        return lines;
    }

    static (List<string> Lines, List<string> Appendix) SplitOffAppendix(List<string> lines)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            if (lines[index].Trim() == AppendixHeading)
            {
                return (lines.GetRange(0, index), lines.GetRange(index, lines.Count - index));
            }
        }
        return (lines, []);
    }

    static List<EntryBlock> SplitEntries(List<string> lines)
    {
        var blocks = new List<EntryBlock>();
        var current = new EntryBlock();
        foreach (var line in lines)
        {
            var id = CorrectionEntryId(line);
            if (id is not null || line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (current.Lines.Count > 0)
                {
                    blocks.Add(current);
                }
                current = new EntryBlock { Id = id ?? "" };
            }
            current.Lines.Add(line);
        }
        if (current.Lines.Count > 0)
        {
            blocks.Add(current);
        }
        return blocks;
    }

    static Dictionary<string, List<string>> ParseDigestBlocks(List<string> appendix)
    {
        var blocks = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var currentId = "";
        var body = new List<string>();
        var inside = false;
        for (var index = 0; index < appendix.Count; index++)
        {
            var line = appendix[index];
            var number = index + 1;
            if (DigestBlockHeading(line) is { } id)
            {
                if (inside)
                {
                    throw new CorrectionsRefusal($"digest block for {currentId} is missing its {BlockClose}");
                }
                if (currentId.Length > 0)
                {
                    throw new CorrectionsRefusal(
                        $"### correction-{currentId} is followed by another heading at appendix line {number} "
                        + $"without a {BlockOpen} of its own");
                }
                if (blocks.ContainsKey(id))
                {
                    throw new CorrectionsRefusal(
                        $"correction {id} has more than one digest block; a second block would silently override the first");
                }
                currentId = id;
                continue;
            }

            if (line.Trim() == BlockOpen)
            {
                if (currentId.Length == 0)
                {
                    throw new CorrectionsRefusal(
                        $"{BlockOpen} at appendix line {number} has no ### correction-<id> heading");
                }
                if (inside)
                {
                    throw new CorrectionsRefusal(
                        $"digest block {currentId} opens a second {BlockOpen} at appendix line {number} "
                        + "before closing the first; everything above it would be discarded");
                }
                inside = true;
                body = [];
                continue;
            }

            if (line.Trim() == BlockClose)
            {
                if (!inside)
                {
                    throw new CorrectionsRefusal($"{BlockClose} at appendix line {number} closes nothing");
                }
                blocks[currentId] = ValidatedBlock(currentId, body);
                currentId = "";
                body = [];
                inside = false;
                continue;
            }

            if (inside)
            {
                body.Add(line);
            }
            else if (blocks.Count > 0 && line.Trim().Length > 0)
            {
                throw new CorrectionsRefusal(
                    $"appendix line {number} is outside any digest block and would be silently dropped: "
                    + GoQuote(TruncateRunes(line.Trim(), 60)));
            }
        }

        if (inside)
        {
            throw new CorrectionsRefusal($"digest block for {currentId} is missing its {BlockClose}");
        }
        if (currentId.Length > 0)
        {
            throw new CorrectionsRefusal($"### correction-{currentId} has no {BlockOpen} block");
        }
        return blocks;
    }

    internal static List<string> ValidatedBlock(string correctionId, List<string> body)
    {
        if (body.Count > 0 && BlockFieldName(body[0]) is null)
        {
            throw new CorrectionsRefusal($"digest block {correctionId} must open with a Rule: line");
        }

        var unknown = new List<string>();
        foreach (var line in body)
        {
            var cut = line.IndexOf(": ", StringComparison.Ordinal);
            if (cut < 0)
            {
                continue;
            }
            var name = line[..cut];
            if (LooksLikeFieldName(name) && BlockFieldName(line) is null)
            {
                unknown.Add(name);
            }
        }
        if (unknown.Count > 0)
        {
            throw new CorrectionsRefusal(
                $"digest block {correctionId} has unrecognised field(s) {QuotedStrings(unknown)}; "
                + $"the fields are {string.Join(", ", BlockFields)}");
        }

        var fields = body.Select(BlockFieldName).Where(field => field is not null).Select(field => field!).ToList();
        foreach (var required in new[] { "Rule", "Action" })
        {
            if (!fields.Contains(required, StringComparer.Ordinal))
            {
                throw new CorrectionsRefusal($"digest block {correctionId} has no {required}: line");
            }
        }
        foreach (var field in fields)
        {
            if (fields.Count(candidate => string.Equals(candidate, field, StringComparison.Ordinal)) > 1)
            {
                throw new CorrectionsRefusal($"digest block {correctionId} repeats the {field}: field");
            }
        }

        var size = BlockCharacterCount(body);
        if (size > BlockCharacterLimit)
        {
            throw new CorrectionsRefusal(
                $"digest block {correctionId} is {size} characters, over the {BlockCharacterLimit} limit; "
                + "shorten it rather than letting it be truncated");
        }
        return body;
    }

    static List<string> ProjectBlock(string id, List<string> block, int sourceLine)
    {
        var rendered = new List<string> { id + ". " + block[0] };
        for (var index = 1; index < block.Count; index++)
        {
            rendered.Add("     " + block[index]);
        }
        rendered.Add($"     Full entry: .ai/corrections.md:{sourceLine}");
        return rendered;
    }

    static void RefuseDigestBlockForShortEntry(
        List<EntryBlock> blocks,
        Dictionary<string, List<string>> digestBlocks)
    {
        var ids = digestBlocks.Keys.ToList();
        ids.Sort(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            var nonblank = blocks
                .Where(block => string.Equals(block.Id, id, StringComparison.Ordinal))
                .SelectMany(block => block.Lines)
                .Count(line => line.Trim().Length > 0);
            if (nonblank <= LinesThatRequireABlock)
            {
                throw new CorrectionsRefusal(
                    $"digest block {id} names a {nonblank}-line entry; blocks are only for entries longer than "
                    + $"{LinesThatRequireABlock} lines. Remove the block so it cannot replace a different short rule by ID.");
            }
        }
    }

    static void RefuseUnsummarisedLongEntry(
        List<EntryBlock> blocks,
        HashSet<string> retired,
        Dictionary<string, List<string>> summaries)
    {
        var unsummarised = new List<string>();
        foreach (var block in blocks)
        {
            if (block.Id.Length == 0 || retired.Contains(block.Id) || summaries.ContainsKey(block.Id))
            {
                continue;
            }
            var nonblank = block.Lines.Count(line => line.Trim().Length > 0);
            if (nonblank > LinesThatRequireABlock)
            {
                unsummarised.Add($"{block.Id} ({nonblank} lines)");
            }
        }
        if (unsummarised.Count > 0)
        {
            throw new CorrectionsRefusal(
                $"correction(s) {string.Join(", ", unsummarised)} are longer than {LinesThatRequireABlock} lines and "
                + $"have no digest block. Add one under {AppendixHeading} in .ai/corrections.md, or tag the entry "
                + "superseded/enforced-by-code if it is history.");
        }
    }

    internal static void RefuseDigestOverBudget(List<string> output)
    {
        if (output.Count > ActiveDigestLineBudget)
        {
            throw new CorrectionsRefusal(
                $"the active digest would be {output.Count} lines, over the {ActiveDigestLineBudget} budget. "
                + "Every session loads this file, so shorten a block, retire an entry the control plane now "
                + "enforces, or raise the budget deliberately with the measurement that justifies it.");
        }
    }

    static List<string> ThemeIndexRegion(List<string> output)
    {
        for (var index = 0; index < output.Count; index++)
        {
            if (CorrectionEntryId(output[index]) is not null || output[index].StartsWith("## ", StringComparison.Ordinal))
            {
                return output.GetRange(0, index);
            }
        }
        return output;
    }

    static void RefuseSecondThemeIndex(List<string> preamble)
    {
        var runs = 0;
        var inside = false;
        foreach (var line in preamble)
        {
            if (CorrectionIndexLine(line) is not null)
            {
                if (!inside)
                {
                    runs++;
                    inside = true;
                }
            }
            else
            {
                inside = false;
            }
        }
        if (runs > 1)
        {
            throw new CorrectionsRefusal(
                $"corrections digest would carry {runs} theme-index blocks; the source has a duplicate index. "
                + "Keep the newest block in .ai/corrections.md and delete the rest.");
        }
    }

    static string? CorrectionEntryId(string line)
    {
        var cut = line.IndexOf(". ", StringComparison.Ordinal);
        if (cut < 0)
        {
            return null;
        }
        var candidate = line[..cut];
        return ValidCorrectionId(candidate) ? candidate : null;
    }

    static string? DigestBlockHeading(string line)
    {
        const string prefix = "### correction-";
        var trimmed = line.TrimEnd('\r');
        if (!trimmed.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }
        var candidate = trimmed[prefix.Length..];
        return ValidCorrectionId(candidate) ? candidate : null;
    }

    static bool ValidCorrectionId(string candidate)
    {
        if (candidate.Length == 0)
        {
            return false;
        }
        var end = 0;
        while (end < candidate.Length && candidate[end] >= '0' && candidate[end] <= '9')
        {
            end++;
        }
        return end > 0
            && (end == candidate.Length
                || (end + 1 == candidate.Length && candidate[end] >= 'a' && candidate[end] <= 'z'));
    }

    static bool StatusTag(string line)
    {
        var trimmed = line.TrimStart(' ', '\t', '\r', '\n');
        return trimmed.StartsWith("[superseded ", StringComparison.Ordinal)
            || trimmed.StartsWith("[enforced-by-code ", StringComparison.Ordinal)
            || trimmed.StartsWith("[dormant ", StringComparison.Ordinal);
    }

    static (string Label, List<string> Numbers)? CorrectionIndexLine(string line)
    {
        if (!line.StartsWith("- ", StringComparison.Ordinal))
        {
            return null;
        }
        var rest = line[2..];
        var index = rest.LastIndexOf(": ", StringComparison.Ordinal);
        if (index < 0)
        {
            return null;
        }
        var label = rest[..index];
        var numbers = rest[(index + 2)..];
        if (!label.StartsWith("**", StringComparison.Ordinal)
            || !label.EndsWith("**", StringComparison.Ordinal)
            || label.Length <= 4)
        {
            return null;
        }
        var parsed = numbers.Split(", ").ToList();
        return parsed.All(ValidCorrectionId) ? (label, parsed) : null;
    }

    static string? BlockFieldName(string line) =>
        BlockFields.FirstOrDefault(field => line.StartsWith(field + ": ", StringComparison.Ordinal));

    static bool LooksLikeFieldName(string name)
    {
        if (name.Length == 0 || name[0] < 'A' || name[0] > 'Z')
        {
            return false;
        }
        foreach (var character in name.AsSpan(1))
        {
            if ((character < 'A' || character > 'Z') && (character < 'a' || character > 'z'))
            {
                return false;
            }
        }
        return true;
    }

    internal static int BlockCharacterCount(List<string> body)
    {
        var size = 0;
        foreach (var line in body)
        {
            var runes = 0;
            foreach (var _ in line.EnumerateRunes())
            {
                runes++;
            }
            size += runes + 1;
        }
        return size;
    }

    static string QuotedStrings(List<string> values) =>
        "[" + string.Join(", ", values.Select(GoQuote)) + "]";

    /// <summary>Go's %q for a string: the quoting the refusal messages were written against.</summary>
    static string GoQuote(string value)
    {
        var builder = new StringBuilder("\"");
        foreach (var character in value)
        {
            switch (character)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                default:
                    if (character < ' ')
                    {
                        builder.Append("\\x").Append(((int)character).ToString("x2"));
                    }
                    else
                    {
                        builder.Append(character);
                    }
                    break;
            }
        }
        return builder.Append('"').ToString();
    }

    static string TruncateRunes(string value, int maximum)
    {
        var runes = value.EnumerateRunes().ToList();
        if (runes.Count <= maximum)
        {
            return value;
        }
        var builder = new StringBuilder();
        for (var index = 0; index < maximum; index++)
        {
            builder.Append(runes[index].ToString());
        }
        return builder.ToString();
    }

    /// <summary>Go's filepath.Join(root, relative): a "." root leaves the relative path bare.</summary>
    static string JoinPath(string root, string relative)
    {
        var joined = root.Length == 0 ? relative : root.TrimEnd('/') + "/" + relative;
        while (joined.StartsWith("./", StringComparison.Ordinal))
        {
            joined = joined[2..];
        }
        return joined.Length == 0 ? "." : joined;
    }

    /// <summary>Go's json.Encoder with two-space indent, HTML escaping off, and a trailing newline.</summary>
    static void WriteJson<T>(T value)
    {
        Console.Out.Write(JsonSerializer.Serialize(
            value,
            new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
        Console.Out.Write('\n');
    }

    static int Fail(string message)
    {
        Console.Error.WriteLine("fern-run: " + message);
        return 2;
    }
}
