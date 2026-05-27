using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Analyzer.Core.Detection.Detectors;

/// <summary>
/// MOD002: Detects null-check patterns that can use null-conditional or coalescing operators.
/// </summary>
public sealed class NullConditionalDetector : IPatternDetector
{
    public string RuleId => "MOD002";
    public string RuleName => "null-conditional";
    public Version MinimumLangVersion => new(6, 0);

    public Task<IReadOnlyList<DetectionResult>> DetectAsync(DetectionContext context, CancellationToken cancellationToken = default)
    {
        var results = new List<DetectionResult>();
        var prefersNullPropagation = DetectorUtilities.PrefersEnabled(context.Standards, "dotnet_style_null_propagation");
        var prefersCoalesce = DetectorUtilities.PrefersEnabled(context.Standards, "dotnet_style_coalesce_expression");

        if (!prefersNullPropagation && !prefersCoalesce)
            return Task.FromResult<IReadOnlyList<DetectionResult>>(results);

        var root = context.SyntaxTree.GetRoot(cancellationToken);

        if (prefersNullPropagation)
            DetectNullPropagation(root, context, cancellationToken, results);

        if (prefersCoalesce)
            DetectCoalesce(root, context, cancellationToken, results);

        return Task.FromResult<IReadOnlyList<DetectionResult>>(results);
    }

    private void DetectNullPropagation(
        SyntaxNode root,
        DetectionContext context,
        CancellationToken cancellationToken,
        List<DetectionResult> results)
    {
        foreach (var ifStatement in root.DescendantNodes().OfType<IfStatementSyntax>())
        {
            if (ifStatement.Else is not null)
                continue;

            if (!TryMatchNullCheck(ifStatement.Condition, SyntaxKind.NotEqualsExpression, out var target))
                continue;

            if (!DetectorUtilities.TryGetSingleStatement(ifStatement.Statement, out var bodyStatement))
                continue;

            if (bodyStatement is not ExpressionStatementSyntax { Expression: InvocationExpressionSyntax invocation })
                continue;

            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
                continue;

            if (!DetectorUtilities.ExpressionsMatch(target, memberAccess.Expression, context.SemanticModel, cancellationToken))
                continue;

            var suggestedCode = $"{memberAccess.Expression}?.{memberAccess.Name}{invocation.ArgumentList};";
            results.Add(DetectorUtilities.CreateResult(
                context,
                this,
                ifStatement,
                "Use null-conditional access instead of an explicit null check",
                suggestedCode));
        }
    }

    private void DetectCoalesce(
        SyntaxNode root,
        DetectionContext context,
        CancellationToken cancellationToken,
        List<DetectionResult> results)
    {
        foreach (var conditional in root.DescendantNodes().OfType<ConditionalExpressionSyntax>())
        {
            if (conditional.Condition is not BinaryExpressionSyntax condition)
                continue;

            ExpressionSyntax? target = null;
            ExpressionSyntax? fallback = null;

            if (condition.IsKind(SyntaxKind.EqualsExpression)
                && TryGetNonNullOperand(condition, out target)
                && DetectorUtilities.ExpressionsMatch(target, conditional.WhenFalse, context.SemanticModel, cancellationToken))
            {
                fallback = conditional.WhenTrue;
            }
            else if (condition.IsKind(SyntaxKind.NotEqualsExpression)
                && TryGetNonNullOperand(condition, out target)
                && DetectorUtilities.ExpressionsMatch(target, conditional.WhenTrue, context.SemanticModel, cancellationToken))
            {
                fallback = conditional.WhenFalse;
            }

            if (target is null || fallback is null)
                continue;

            var suggestedCode = $"{target} ?? {fallback}";
            results.Add(DetectorUtilities.CreateResult(
                context,
                this,
                conditional,
                "Use the null-coalescing operator instead of a null-check conditional",
                suggestedCode));
        }
    }

    private static bool TryMatchNullCheck(ExpressionSyntax expression, SyntaxKind kind, out ExpressionSyntax target)
    {
        target = null!;

        if (expression is not BinaryExpressionSyntax condition || !condition.IsKind(kind))
            return false;

        return TryGetNonNullOperand(condition, out target);
    }

    private static bool TryGetNonNullOperand(BinaryExpressionSyntax condition, out ExpressionSyntax target)
    {
        target = null!;

        if (DetectorUtilities.IsNullLiteral(condition.Left) && !DetectorUtilities.IsNullLiteral(condition.Right))
        {
            target = condition.Right;
            return true;
        }

        if (DetectorUtilities.IsNullLiteral(condition.Right) && !DetectorUtilities.IsNullLiteral(condition.Left))
        {
            target = condition.Left;
            return true;
        }

        return false;
    }
}
