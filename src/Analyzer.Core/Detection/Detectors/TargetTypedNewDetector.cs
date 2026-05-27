using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Analyzer.Core.Detection.Detectors;

/// <summary>
/// MOD009: Detects explicit object creation where target-typed new can be used.
/// </summary>
public sealed class TargetTypedNewDetector : IPatternDetector
{
    public string RuleId => "MOD009";
    public string RuleName => "target-typed-new";
    public Version MinimumLangVersion => new(9, 0);

    public Task<IReadOnlyList<DetectionResult>> DetectAsync(DetectionContext context, CancellationToken cancellationToken = default)
    {
        var results = new List<DetectionResult>();
        var root = context.SyntaxTree.GetRoot(cancellationToken);

        foreach (var declaration in root.DescendantNodes().OfType<VariableDeclarationSyntax>())
        {
            if (declaration.Type.IsVar || declaration.Variables.Count != 1)
                continue;

            var variable = declaration.Variables[0];
            if (variable.Initializer?.Value is not ObjectCreationExpressionSyntax creation)
                continue;

            TryAddResult(context, cancellationToken, results, declaration.Type, creation, variable);
        }

        foreach (var property in root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
        {
            if (property.Initializer?.Value is not ObjectCreationExpressionSyntax creation)
                continue;

            TryAddResult(context, cancellationToken, results, property.Type, creation, property);
        }

        return Task.FromResult<IReadOnlyList<DetectionResult>>(results);
    }

    private void TryAddResult(
        DetectionContext context,
        CancellationToken cancellationToken,
        List<DetectionResult> results,
        TypeSyntax declaredTypeSyntax,
        ObjectCreationExpressionSyntax creation,
        SyntaxNode node)
    {
        var declaredType = context.SemanticModel.GetTypeInfo(declaredTypeSyntax, cancellationToken).Type;
        var createdType = context.SemanticModel.GetTypeInfo(creation, cancellationToken).Type;

        if (declaredType is null || createdType is null)
            return;

        if (!SymbolEqualityComparer.Default.Equals(declaredType, createdType))
            return;

        var suggestedCode = BuildSuggestedCode(creation);
        results.Add(DetectorUtilities.CreateResult(
            context,
            this,
            node,
            "Use target-typed new when the type is already declared on the left-hand side",
            suggestedCode,
            originalCode: creation.ToString()));
    }

    private static string BuildSuggestedCode(ObjectCreationExpressionSyntax creation)
    {
        var arguments = creation.ArgumentList?.ToString() ?? "()";
        var initializer = creation.Initializer is null ? string.Empty : $" {creation.Initializer}";
        return $"new{arguments}{initializer}";
    }
}
