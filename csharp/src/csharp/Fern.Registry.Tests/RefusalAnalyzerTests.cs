using System.Collections.Immutable;
using Fern.Registry.Analyzer;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace Fern.Registry.Tests;

public sealed class RefusalAnalyzerTests
{
    [Fact]
    public void Ignoring_a_registry_result_fails_the_build()
    {
        const string source =
            """
            using Fern.Registry;
            public static class Probe
            {
                public static void Ignore()
                {
                    RegistryResult.Ok();
                }
            }
            """;

        var diagnostics = Analyze(source);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == UnusedRegistryResultAnalyzer.DiagnosticId);
    }

    [Fact]
    public void Observing_a_registry_result_does_not_fail_the_build()
    {
        const string source =
            """
            using Fern.Registry;
            public static class Probe
            {
                public static bool Observe()
                {
                    var result = RegistryResult.Ok();
                    return result.IsOk;
                }
            }
            """;

        var diagnostics = Analyze(source);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == UnusedRegistryResultAnalyzer.DiagnosticId);
    }

    [Fact]
    public void A_sqlite_connection_outside_registry_home_fails_the_build()
    {
        const string source =
            """
            namespace Fern.Registry;
            public static class Probe
            {
                public static object Open() => new Microsoft.Data.Sqlite.SqliteConnection("file:probe");
            }
            """;

        var diagnostics = Analyze(source, new DirectSqliteConnectionAnalyzer());
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == DirectSqliteConnectionAnalyzer.DiagnosticId);
    }

    [Fact]
    public void Registry_home_may_construct_a_sqlite_connection()
    {
        const string source =
            """
            namespace Fern.Registry;
            public static class RegistryHome
            {
                public static object Open() => new Microsoft.Data.Sqlite.SqliteConnection("file:home");
            }
            """;

        var diagnostics = Analyze(source, new DirectSqliteConnectionAnalyzer());
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == DirectSqliteConnectionAnalyzer.DiagnosticId);
    }

    private static ImmutableArray<Diagnostic> Analyze(string source) =>
        Analyze(source, new UnusedRegistryResultAnalyzer());

    private static ImmutableArray<Diagnostic> Analyze(string source, DiagnosticAnalyzer analyzer)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(Path.Combine(runtime, "System.Runtime.dll")),
            MetadataReference.CreateFromFile(typeof(Fern.Registry.RegistryResult).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Microsoft.Data.Sqlite.SqliteConnection).Assembly.Location),
        };
        var compilation = CSharpCompilation.Create(
            "refusal-analyzer-test",
            [tree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var compilationWithAnalyzers = compilation.WithAnalyzers(ImmutableArray.Create(analyzer));
        return compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult();
    }
}
