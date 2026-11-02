using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Fern.Registry.Analyzer;

/// <summary>
/// A second SQLite connection is a second writer. RegistryHome is the one
/// place that may construct one; a sentence in a file header cannot keep that.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DirectSqliteConnectionAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "FERNREG002";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "SqliteConnection must be opened by RegistryHome",
        "new SqliteConnection is a second writer; open Registry Home through RegistryHome",
        "Fern.Registry",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeCreation, SyntaxKind.ObjectCreationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeCreation, SyntaxKind.ImplicitObjectCreationExpression);
    }

    private static void AnalyzeCreation(SyntaxNodeAnalysisContext context)
    {
        var type = context.SemanticModel.GetTypeInfo(context.Node, context.CancellationToken).Type;
        if (type is null || type.Name != "SqliteConnection")
        {
            return;
        }

        if (type.ContainingNamespace.ToDisplayString() != "Microsoft.Data.Sqlite")
        {
            return;
        }

        var containingType = context.ContainingSymbol?.ContainingType;
        if (containingType is { Name: "RegistryHome" or "RegistrySchema", ContainingNamespace.Name: "Registry" })
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, context.Node.GetLocation()));
    }
}
