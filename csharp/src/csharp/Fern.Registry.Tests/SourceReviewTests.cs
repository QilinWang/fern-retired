using System.Text.RegularExpressions;
using Xunit;

namespace Fern.Registry.Tests;

public sealed class SourceReviewTests
{
    [Fact]
    public void Production_csharp_has_no_deferred_begin()
    {
        foreach (var line in ProductionLines())
        {
            if (line.Contains("BeginTransaction", StringComparison.Ordinal)
                && !line.Contains("deferred: true", StringComparison.Ordinal))
            {
                Assert.Fail($"write transactions use RegistryWriter.WriteImmediate; a non-deferred BeginTransaction is: {line}");
            }

            if (Regex.IsMatch(line, "\"BEGIN(?! IMMEDIATE)")
                && !line.Contains("BEGIN IMMEDIATE", StringComparison.Ordinal))
            {
                Assert.Fail($"BEGIN that is not IMMEDIATE: {line}");
            }
        }
    }

    [Fact]
    public void Production_csharp_registry_reads_never_use_GetDouble_DateTime_or_julianday()
    {
        foreach (var line in ProductionLines())
        {
            Assert.DoesNotMatch(@"GetDouble\s*\(\s*\d", line);
            Assert.DoesNotContain("GetDateTime", line, StringComparison.Ordinal);
            Assert.DoesNotContain("julianday", line, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// CHG 2026-09-10: GetBoolean joins the list, and it is the reason this guard
    /// keeps catching things after the fact instead of before.
    ///
    /// SQLite has no boolean storage class. This schema spells the concept three
    /// ways across four tables — `INTEGER ... CHECK (x IN (0,1))`, `BOOL`, and
    /// `BOOLEAN` — and the last two contain no "INT", so they take NUMERIC
    /// affinity rather than INTEGER. Every one of them was read with GetBoolean,
    /// which works and states a type the database does not have. Six instances
    /// were found and fixed one at a time over two days; this stops the seventh.
    ///
    /// It is not cosmetic. Microsoft.Data.Sqlite reports such a column as Int64
    /// when rows exist and String when the result is empty, which is how a
    /// converted read shipped working in every test and crashing `fern-run site
    /// list` on a fresh Registry.
    /// </summary>
    [Fact]
    public void Production_csharp_registry_reads_never_use_GetBoolean()
    {
        foreach (var line in ProductionLines())
        {
            // A database reader's GetBoolean takes an ordinal; JsonElement's
            // takes nothing. Only the first is reading a column, and only the
            // first is the defect — reading a JSON boolean is exactly right.
            //
            // Comments are skipped, and that is deliberate rather than lazy: the
            // fixes for this class carry notes quoting the code they replaced,
            // and a guard that fires on the explanation of a fix teaches people
            // to reword the explanation.
            // ProductionLines prefixes each line with "<file>:", so the code
            // starts after that — trimming the whole string reads the path.
            var marker = line.IndexOf(".cs:", StringComparison.Ordinal);
            var code = (marker < 0 ? line : line[(marker + 4)..]).TrimStart();
            if (code.StartsWith("//", StringComparison.Ordinal)
                || code.StartsWith("*", StringComparison.Ordinal))
            {
                continue;
            }

            Assert.DoesNotMatch(@"GetBoolean\s*\(\s*[^)\s]", line);
        }
    }


    [Fact]
    public void Active_test_code_does_not_reopen_retired_Rust_implementations()
    {
        var repoRoot = ProductionHome.FindRepoRoot();
        var forbiddenSymbols = new[]
        {
            "Rust" + "Fixtures",
            "Rust" + "Witness",
            "RustBinary" + "Missing",
            "ReadRepositoryBytes" + "AtCommit",
            "RUST_" + "SOURCE",
            "RUST_CLI_TEST_" + "SOURCES",
        };
        var forbiddenReads = new[]
        {
            new Regex(
                @"File\.(ReadAllText|ReadAllBytes)\s*\([^;]*retired/",
                RegexOptions.CultureInvariant | RegexOptions.Singleline),
            new Regex(
                @"os\.ReadFile\s*\([^\n]*retired/",
                RegexOptions.CultureInvariant),
        };
        var retiredFixtureNames = new[]
        {
            "rust-" + "goldens",
            "model_card_" + "rust_golden.json",
            "scheduler-cycle-observation-" + "rust-golden.json",
            ".rust-" + "receipt.json",
        };
        var retiredExecutable =
            "Path.Combine(TestRegistry.RepoRoot(), \"target\", \"debug\", \"fern-run\")";

        var sourceRoots = new[]
        {
            Path.Combine(repoRoot, "src", "csharp"),
            Path.Combine(repoRoot, "tests"),
            Path.Combine(repoRoot, "tools"),
        };
        var sourceExtensions = new HashSet<string>(StringComparer.Ordinal)
        {
            ".cs", ".py", ".go",
        };
        foreach (var sourceRoot in sourceRoots)
        {
            if (!Directory.Exists(sourceRoot))
            {
                continue;
            }
            foreach (var file in Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories))
            {
                if (!sourceExtensions.Contains(Path.GetExtension(file))
                    || file.Contains($"{Path.DirectorySeparatorChar}retired{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }
                var source = File.ReadAllText(file);
                foreach (var symbol in forbiddenSymbols)
                {
                    Assert.DoesNotContain(symbol, source, StringComparison.Ordinal);
                }
                foreach (var retiredRead in forbiddenReads)
                {
                    Assert.DoesNotMatch(retiredRead, source);
                }
                foreach (var retiredFixtureName in retiredFixtureNames)
                {
                    Assert.DoesNotContain(retiredFixtureName, source, StringComparison.Ordinal);
                }
                Assert.DoesNotContain(retiredExecutable, source, StringComparison.Ordinal);
            }
        }
    }

    private static IEnumerable<string> ProductionLines()
    {
        var root = Path.Combine(ProductionHome.FindRepoRoot(), "src", "csharp", "Fern.Registry");
        foreach (var file in Directory.GetFiles(root, "*.cs"))
        {
            foreach (var line in File.ReadAllLines(file))
            {
                yield return $"{file}:{line}";
            }
        }
    }
}
