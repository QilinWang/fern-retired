using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Fern.Registry.Analyzer;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UnusedRegistryResultAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "FERNREG001";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "RegistryResult must be observed",
        "RegistryResult value is discarded; a refusal would be silent",
        "Fern.Registry",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeExpressionStatement, SyntaxKind.ExpressionStatement);
        context.RegisterSyntaxNodeAction(AnalyzeAssignment, SyntaxKind.SimpleAssignmentExpression);
    }

    private static void AnalyzeExpressionStatement(SyntaxNodeAnalysisContext context)
    {
        if (context.Node is not ExpressionStatementSyntax statement)
        {
            return;
        }

        ReportIfRegistryResult(context, statement.Expression, statement.GetLocation());
    }

    private static void AnalyzeAssignment(SyntaxNodeAnalysisContext context)
    {
        if (context.Node is not AssignmentExpressionSyntax assignment)
        {
            return;
        }

        if (assignment.Left is IdentifierNameSyntax { Identifier.ValueText: "_" })
        {
            ReportIfRegistryResult(context, assignment.Right, assignment.GetLocation());
        }
    }

    private static void ReportIfRegistryResult(SyntaxNodeAnalysisContext context, ExpressionSyntax expression, Location location)
    {
        var type = context.SemanticModel.GetTypeInfo(expression, context.CancellationToken).Type;
        if (type is null)
        {
            return;
        }

        if (IsRegistryResult(type))
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, location));
        }
    }

    private static bool IsRegistryResult(ITypeSymbol type)
    {
        if (type.Name != "RegistryResult")
        {
            return false;
        }

        return type.ContainingNamespace.ToDisplayString() == "Fern.Registry";
    }
}
