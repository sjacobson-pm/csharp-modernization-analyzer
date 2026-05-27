using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Analyzer.Core.Detection.Detectors;

/// <summary>
/// MOD007: Detects patterns that can be simplified with the ??= operator.
/// </summary>
public sealed class NullCoalescingAssignmentDetector : IPatternDetector
{
    public string RuleId => "MOD007";
    public string RuleName => "null-coalescing-assignment";
    public Version MinimumLangVersion => new(8, 0);

    public Task<IReadOnlyList<DetectionResult>> DetectAsync(DetectionContext context, CancellationToken cancellationToken = default)
    {
        var results = new List<DetectionResult>();
        var root = context.SyntaxTree.GetRoot(cancellationToken);

        DetectIfAssignments(root, context, cancellationToken, results);
        DetectCoalesceAssignments(root, context, cancellationToken, results);

        return Task.FromResult<IReadOnlyList<DetectionResult>>(results);
    }

    private void DetectIfAssignments(
        SyntaxNode root,
        DetectionContext context,
        CancellationToken cancellationToken,
        List<DetectionResult> results)
    {
        foreach (var ifStatement in root.DescendantNodes().OfType<IfStatementSyntax>())
        {
            if (ifStatement.Else is not null)
                continue;

            if (!TryGetNullCheckedTarget(ifStatement.Condition, out var target))
                continue;

            if (!DetectorUtilities.TryGetSimpleAssignment(ifStatement.Statement, out var assignment))
                continue;

            if (!DetectorUtilities.ExpressionsMatch(target, assignment.Left, context.SemanticModel, cancellationToken))
                continue;

            var suggestedCode = $"{assignment.Left} ??= {assignment.Right};";
            results.Add(DetectorUtilities.CreateResult(
                context,
                this,
                ifStatement,
                "Use the null-coalescing assignment operator instead of an explicit null check",
                suggestedCode));
        }
    }

    private void DetectCoalesceAssignments(
        SyntaxNode root,
        DetectionContext context,
        CancellationToken cancellationToken,
        List<DetectionResult> results)
    {
        foreach (var assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (!assignment.IsKind(SyntaxKind.SimpleAssignmentExpression))
                continue;

            if (assignment.Right is not BinaryExpressionSyntax { RawKind: (int)SyntaxKind.CoalesceExpression } coalesce)
                continue;

            if (!DetectorUtilities.ExpressionsMatch(assignment.Left, coalesce.Left, context.SemanticModel, cancellationToken))
                continue;

            var suggestedCode = $"{assignment.Left} ??= {coalesce.Right}";
            results.Add(DetectorUtilities.CreateResult(
                context,
                this,
                assignment,
                "Use the null-coalescing assignment operator instead of assigning a coalesce expression",
                suggestedCode));
        }
    }

    private static bool TryGetNullCheckedTarget(ExpressionSyntax expression, out ExpressionSyntax target)
    {
        target = null!;

        if (expression is not BinaryExpressionSyntax comparison || !comparison.IsKind(SyntaxKind.EqualsExpression))
            return false;

        if (DetectorUtilities.IsNullLiteral(comparison.Left) && !DetectorUtilities.IsNullLiteral(comparison.Right))
        {
            target = comparison.Right;
            return true;
        }

        if (DetectorUtilities.IsNullLiteral(comparison.Right) && !DetectorUtilities.IsNullLiteral(comparison.Left))
        {
            target = comparison.Left;
            return true;
        }

        return false;
    }
}
