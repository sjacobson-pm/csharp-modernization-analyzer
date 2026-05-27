using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Analyzer.Core.Detection.Detectors;

/// <summary>
/// MOD010: Detects collection creation patterns that can use collection expressions.
/// </summary>
public sealed class CollectionExpressionDetector : IPatternDetector
{
    public string RuleId => "MOD010";
    public string RuleName => "collection-expression";
    public Version MinimumLangVersion => new(12, 0);

    public Task<IReadOnlyList<DetectionResult>> DetectAsync(DetectionContext context, CancellationToken cancellationToken = default)
    {
        var results = new List<DetectionResult>();
        var root = context.SyntaxTree.GetRoot(cancellationToken);

        foreach (var creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            if (!HasExplicitTargetContext(creation))
                continue;

            var createdType = context.SemanticModel.GetTypeInfo(creation, cancellationToken).Type as INamedTypeSymbol;
            if (createdType is null)
                continue;

            if (createdType.Name != "List" || createdType.ContainingNamespace.ToDisplayString() != "System.Collections.Generic")
                continue;

            if (creation.Initializer is null)
                continue;

            if (creation.ArgumentList is { Arguments.Count: > 0 })
                continue;

            var items = string.Join(", ", creation.Initializer.Expressions.Select(expression => expression.ToString()));
            results.Add(DetectorUtilities.CreateResult(
                context,
                this,
                creation,
                "Use a collection expression instead of creating a List with an initializer",
                $"[{items}]"));
        }

        foreach (var creation in root.DescendantNodes().OfType<ArrayCreationExpressionSyntax>())
        {
            if (!HasExplicitTargetContext(creation) || creation.Initializer is null)
                continue;

            var items = string.Join(", ", creation.Initializer.Expressions.Select(expression => expression.ToString()));
            results.Add(DetectorUtilities.CreateResult(
                context,
                this,
                creation,
                "Use a collection expression instead of an array initializer",
                $"[{items}]"));
        }

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (!HasExplicitTargetContext(invocation))
                continue;

            var symbol = context.SemanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol as IMethodSymbol;
            if (symbol is null || symbol.Name != nameof(Array.Empty) || symbol.ContainingType.SpecialType != SpecialType.System_Array)
                continue;

            if (invocation.ArgumentList.Arguments.Count != 0)
                continue;

            results.Add(DetectorUtilities.CreateResult(
                context,
                this,
                invocation,
                "Use an empty collection expression instead of Array.Empty<T>()",
                "[]"));
        }

        return Task.FromResult<IReadOnlyList<DetectionResult>>(results);
    }

    private static bool HasExplicitTargetContext(ExpressionSyntax expression)
    {
        return expression.Parent switch
        {
            EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Type: { } typeSyntax } } } => !typeSyntax.IsVar,
            EqualsValueClauseSyntax { Parent: PropertyDeclarationSyntax } => true,
            ReturnStatementSyntax => true,
            _ => false
        };
    }
}
