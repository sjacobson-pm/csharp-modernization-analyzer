using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Analyzer.Core.Detection.Detectors;

/// <summary>
/// MOD005: Detects if/else chains that can be replaced with switch expressions.
/// </summary>
public sealed class SwitchExpressionDetector : IPatternDetector
{
    public string RuleId => "MOD005";

    public string RuleName => "switch-expression";

    public Version MinimumLangVersion => new(8, 0);

    public Task<IReadOnlyList<DetectionResult>> DetectAsync(DetectionContext context, CancellationToken cancellationToken = default)
    {
        var results = new List<DetectionResult>();
        var root = context.SyntaxTree.GetRoot(cancellationToken);

        foreach (var ifStatement in root.DescendantNodes().OfType<IfStatementSyntax>())
        {
            if (ifStatement.Parent is ElseClauseSyntax) { continue; }

            if (!TryBuildSuggestion(ifStatement, context, cancellationToken, out var suggestedCode)) { continue; }

            results.Add(
                DetectorUtilities.CreateResult(
                    context,
                    this,
                    ifStatement,
                    "Replace repeated if/else checks with a switch expression",
                    suggestedCode));
        }

        return Task.FromResult<IReadOnlyList<DetectionResult>>(results);
    }

    private static bool TryBuildSuggestion(
        IfStatementSyntax rootIf,
        DetectionContext context,
        CancellationToken cancellationToken,
        out string suggestedCode)
    {
        suggestedCode = string.Empty;
        var branches = new List<(ExpressionSyntax Pattern, ExpressionSyntax Result)>();
        ExpressionSyntax? discriminant = null;
        ExpressionSyntax? defaultResult;
        ExpressionSyntax? assignmentTarget = null;
        var mode = BranchMode.Unknown;
        var current = rootIf;

        while (true)
        {
            if (!TryMatchComparison(current.Condition, context.SemanticModel, cancellationToken, out var branchDiscriminant, out var pattern))
            {
                return false;
            }

            if (discriminant is null) { discriminant = branchDiscriminant; }
            else if (!DetectorUtilities.ExpressionsMatch(discriminant, branchDiscriminant, context.SemanticModel, cancellationToken))
            {
                return false;
            }

            if (!TryGetBranchResult(
                    current.Statement,
                    ref mode,
                    ref assignmentTarget,
                    context.SemanticModel,
                    cancellationToken,
                    out var resultExpression)) { return false; }

            branches.Add((pattern, resultExpression));

            if (current.Else is null) { return false; }

            if (current.Else.Statement is IfStatementSyntax nextIf)
            {
                current = nextIf;

                continue;
            }

            if (!TryGetBranchResult(
                    current.Else.Statement,
                    ref mode,
                    ref assignmentTarget,
                    context.SemanticModel,
                    cancellationToken,
                    out defaultResult)) { return false; }

            break;
        }

        if (branches.Count < 3 || mode == BranchMode.Unknown) { return false; }

        var armLines = branches.Select(branch => $"    {branch.Pattern} => {branch.Result},");
        var defaultLine = $"    _ => {defaultResult}";
        var body = string.Join(Environment.NewLine, armLines.Append(defaultLine));

        suggestedCode = mode == BranchMode.Return
            ? $"return {discriminant} switch{Environment.NewLine}{{{Environment.NewLine}{body}{Environment.NewLine}}};"
            : $"{assignmentTarget} = {discriminant} switch{Environment.NewLine}{{{Environment.NewLine}{body}{Environment.NewLine}}};";

        return true;
    }

    private static bool TryMatchComparison(
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        CancellationToken cancellationToken,
        out ExpressionSyntax discriminant,
        out ExpressionSyntax pattern)
    {
        discriminant = null!;
        pattern = null!;

        if (expression is not BinaryExpressionSyntax comparison || !comparison.IsKind(SyntaxKind.EqualsExpression)) { return false; }

        if (IsConstantLike(comparison.Left, semanticModel, cancellationToken) && !IsConstantLike(comparison.Right, semanticModel, cancellationToken))
        {
            discriminant = comparison.Right;
            pattern = comparison.Left;

            return true;
        }

        if (IsConstantLike(comparison.Right, semanticModel, cancellationToken) && !IsConstantLike(comparison.Left, semanticModel, cancellationToken))
        {
            discriminant = comparison.Left;
            pattern = comparison.Right;

            return true;
        }

        return false;
    }

    private static bool IsConstantLike(ExpressionSyntax expression, SemanticModel semanticModel, CancellationToken cancellationToken)
    {
        expression = DetectorUtilities.Unwrap(expression);

        if (expression.IsKind(SyntaxKind.NullLiteralExpression)) { return true; }

        if (semanticModel.GetConstantValue(expression, cancellationToken).HasValue) { return true; }

        var symbol = semanticModel.GetSymbolInfo(expression, cancellationToken).Symbol;

        return symbol is IFieldSymbol { HasConstantValue: true } or IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum };
    }

    private static bool TryGetBranchResult(
        StatementSyntax statement,
        ref BranchMode mode,
        ref ExpressionSyntax? assignmentTarget,
        SemanticModel semanticModel,
        CancellationToken cancellationToken,
        out ExpressionSyntax resultExpression)
    {
        resultExpression = null!;

        if (!DetectorUtilities.TryGetSingleStatement(statement, out var singleStatement)) { return false; }

        if (singleStatement is ReturnStatementSyntax { Expression: { } returnExpression })
        {
            if (mode == BranchMode.Assignment) { return false; }

            mode = BranchMode.Return;
            resultExpression = returnExpression;

            return true;
        }

        if (singleStatement is not ExpressionStatementSyntax
            {
                Expression: AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.SimpleAssignmentExpression } assignment,
            }) { return false; }

        if (mode == BranchMode.Return) { return false; }

        if (assignmentTarget is null) { assignmentTarget = assignment.Left; }
        else if (!DetectorUtilities.ExpressionsMatch(assignmentTarget, assignment.Left, semanticModel, cancellationToken)) { return false; }

        mode = BranchMode.Assignment;
        resultExpression = assignment.Right;

        return true;
    }

    private enum BranchMode
    {
        Unknown,
        Return,
        Assignment,
    }
}
