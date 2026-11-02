using System.Text.RegularExpressions;
using Xunit;

namespace Fern.Registry.Tests;

/// <summary>
/// Every column named by a hand-written INSERT in a test must exist in
/// Schema.sql.
///
/// This exists because of a specific two-day failure. `experiment_matrices` lost
/// its `recipe_hash` column when the Recipe runtime retired on 2026-09-08
/// (1e918d06). A hand-written INSERT in `GoOnlyDutyOfficerTests` dated
/// 2026-08-28 still named it, so five tests failed from that day on, and the
/// only signal was a SQLite runtime error naming one column — which reads like
/// a broken test rather than an unfinished retirement.
///
/// The general shape: tests set state up by writing rows directly instead of
/// calling the authority that owns the table, and there are 559 such statements
/// across 58 tables. No compiler sees them, because they are strings, so a
/// dropped or renamed column is found only when that particular test runs.
/// Routing every one of them through its authority is the real fix and is a
/// long migration; this is the cheap guard that makes the next schema change
/// fail here, in one place, with the table and column named, instead of
/// scattered across whichever tests happen to touch it.
/// </summary>
public sealed class TestSqlNamesRealColumnsTests
{
    [Fact]
    public void EveryColumnAHandWrittenTestInsertNamesExistsInTheSchema()
    {
        var root = ProductionHome.FindRepoRoot();
        var schema = Columns(File.ReadAllText(Path.Combine(root, "src", "csharp", "Fern.Registry", "Schema.sql")));
        Assert.NotEmpty(schema);

        var problems = new List<string>();
        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(root, "src", "csharp"), "*.cs", SearchOption.AllDirectories))
        {
            if (!file.Contains("Tests", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.EndsWith(nameof(TestSqlNamesRealColumnsTests) + ".cs", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (Match insert in InsertWithColumns.Matches(File.ReadAllText(file)))
            {
                var table = insert.Groups["table"].Value;
                // A table this file does not create is not this guard's business:
                // tests build their own scratch tables too, and Schema.sql is the
                // only authority being checked.
                if (!schema.TryGetValue(table, out var known))
                {
                    continue;
                }

                foreach (var column in insert.Groups["columns"].Value
                             .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!Identifier.IsMatch(column) || known.Contains(column))
                    {
                        continue;
                    }

                    problems.Add($"{Path.GetRelativePath(root, file)}: {table} has no column {column}");
                }
            }
        }

        Assert.True(
            problems.Count == 0,
            "a test writes a column the schema does not have, which SQLite reports only when that\n"
            + "test runs; the column was probably dropped by a retirement that did not grep for it:\n  "
            + string.Join("\n  ", problems));
    }

    static readonly Regex InsertWithColumns = new(
        @"INSERT\s+(?:OR\s+\w+\s+)?INTO\s+(?<table>\w+)\s*\((?<columns>[^)]*)\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly Regex Identifier = new(@"^[a-z_][a-z0-9_]*$", RegexOptions.Compiled);

    static readonly Regex TableHead = new(
        @"CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?(?<table>\w+)\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The body is scanned by parenthesis depth and split on top-level commas,
    /// not by line. Both matter, and the first version of this guard got both
    /// wrong: <c>artifact_snapshots</c> ends with <c>, run_hash TEXT);</c>, so a
    /// column shares a line with the table's closing bracket and begins with a
    /// comma, and a line-based reader saw neither the column nor the end of the
    /// table. A <c>CHECK (x &gt; 0)</c> or <c>VARCHAR(10)</c> also puts brackets
    /// inside the body, which is why depth is tracked rather than matched.
    /// </summary>
    static Dictionary<string, HashSet<string>> Columns(string schemaText)
    {
        var text = string.Join('\n', schemaText.Split('\n').Select(StripComment));
        var tables = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (Match head in TableHead.Matches(text))
        {
            var depth = 1;
            var index = head.Index + head.Length;
            var body = new System.Text.StringBuilder();
            while (index < text.Length && depth > 0)
            {
                var character = text[index++];
                if (character == '(') depth++;
                else if (character == ')') depth--;
                if (depth > 0) body.Append(character);
            }

            var columns = new HashSet<string>(StringComparer.Ordinal);
            foreach (var part in SplitTopLevel(body.ToString()))
            {
                var name = part.Trim().Split([' ', '\t', '\n'], 2)[0].Trim();
                if (Identifier.IsMatch(name) && !Constraints.Contains(name))
                {
                    columns.Add(name);
                }
            }

            tables[head.Groups["table"].Value] = columns;
        }

        return tables;
    }

    static string StripComment(string line)
    {
        var comment = line.IndexOf("--", StringComparison.Ordinal);
        return comment < 0 ? line : line[..comment];
    }

    static IEnumerable<string> SplitTopLevel(string body)
    {
        var depth = 0;
        var part = new System.Text.StringBuilder();
        foreach (var character in body)
        {
            if (character == '(') depth++;
            else if (character == ')') depth--;
            if (character == ',' && depth == 0)
            {
                yield return part.ToString();
                part.Clear();
                continue;
            }

            part.Append(character);
        }

        yield return part.ToString();
    }

    static readonly HashSet<string> Constraints = new(StringComparer.OrdinalIgnoreCase)
    {
        "PRIMARY", "FOREIGN", "UNIQUE", "CHECK", "CONSTRAINT",
    };
}
