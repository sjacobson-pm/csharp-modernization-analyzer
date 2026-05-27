using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Analyzer.Core.Detection.Detectors;

/// <summary>
/// MOD004: Detects 'is' with cast and 'as' with null check patterns
/// that can be replaced with modern pattern matching.
/// </summary>
public sealed class PatternMatchingDetector : IPatternDetector
{
    public string RuleId => "MOD004";

    public string RuleName => "pattern-matching";

    public Version MinimumLangVersion => new(7, 0);

    public Task<IReadOnlyList<DetectionResult>> DetectAsync(DetectionContext context, CancellationToken cancellationToken = default)
    {
        var results = new List<DetectionResult>();

        if (context.Standards.IsStyleDisabled("csharp_style_pattern_matching_over_is_with_cast_check") &&
            context.Standards.IsStyleDisabled("csharp_style_pattern_matching_over_as_with_null_check"))
        {
            return Task.FromResult<IReadOnlyList<DetectionResult>>(results);
        }

        var root = context.SyntaxTree.GetRoot(cancellationToken);

        this.DetectIsWithCast(root, context, results);
        this.DetectAsWithNullCheck(root, context, results, cancellationToken);

        return Task.FromResult<IReadOnlyList<DetectionResult>>(results);
    }

    private void DetectIsWithCast(SyntaxNode root, DetectionContext context, List<DetectionResult> results)
    {
        if (context.Standards.IsStyleDisabled("csharp_style_pattern_matching_over_is_with_cast_check")) { return; }

        var ifStatements = root.DescendantNodes().OfType<IfStatementSyntax>();

        foreach (var ifStatement in ifStatements)
        {
            if (ifStatement.Condition is not BinaryExpressionSyntax isExpr || !isExpr.IsKind(SyntaxKind.IsExpression)) { continue; }

            if (isExpr.Right is not TypeSyntax typeSyntax) { continue; }

            var body = ifStatement.Statement;

            var casts = body.DescendantNodes()
                            .OfType<CastExpressionSyntax>()
                            .Where(cast => cast.Type.ToString() == typeSyntax.ToString() && cast.Expression.ToString() == isExpr.Left.ToString())
                            .ToList();

            if (casts.Count == 0) { continue; }

            var variableName = TryFindIntroducedVariableName(ifStatement.Statement, isExpr.Left.ToString(), typeSyntax.ToString()) ?? "typed";
            var rewrittenStatement = RewriteIsPatternBody(ifStatement.Statement, isExpr.Left.ToString(), typeSyntax.ToString(), variableName);

            var updatedIf = ifStatement.WithCondition(SyntaxFactory.ParseExpression($"{isExpr.Left} is {typeSyntax} {variableName}"))
                                       .WithStatement(rewrittenStatement)
                                       .NormalizeWhitespace()
                                       .WithLeadingTrivia(ifStatement.GetLeadingTrivia())
                                       .WithTrailingTrivia(ifStatement.GetTrailingTrivia());

            results.Add(
                new DetectionResult
                {
                    RuleId = this.RuleId,
                    RuleName = this.RuleName,
                    Description = "Replace 'is' check + cast with pattern matching",
                    FilePath = context.FilePath,
                    Span = ifStatement.Span,
                    LineSpan = ifStatement.GetLocation().GetLineSpan().Span,
                    OriginalCode = ifStatement.ToFullString(),
                    SuggestedCode = updatedIf.ToFullString(),
                    Severity = Severity.Suggestion,
                });
        }
    }

    private void DetectAsWithNullCheck(SyntaxNode root, DetectionContext context, List<DetectionResult> results, CancellationToken cancellationToken)
    {
        if (context.Standards.IsStyleDisabled("csharp_style_pattern_matching_over_as_with_null_check")) { return; }

        var localDeclarations = root.DescendantNodes().OfType<LocalDeclarationStatementSyntax>();

        foreach (var localDecl in localDeclarations)
        {
            var variable = localDecl.Declaration.Variables.FirstOrDefault();

            if (variable?.Initializer?.Value is not BinaryExpressionSyntax asExpr || !asExpr.IsKind(SyntaxKind.AsExpression)) { continue; }

            if (asExpr.Right is not TypeSyntax typeSyntax) { continue; }

            var varName = variable.Identifier.Text;
            var parent = localDecl.Parent;

            if (parent is null) { continue; }

            var siblings = parent.ChildNodes().ToList();
            var declIndex = siblings.IndexOf(localDecl);

            if (declIndex < 0 || declIndex + 1 >= siblings.Count) { continue; }

            var nextStatement = siblings[declIndex + 1];

            if (nextStatement is not IfStatementSyntax ifStatement) { continue; }

            if (!IsNullCheckForVariable(ifStatement.Condition, varName)) { continue; }

            var replacementSpan = TextSpan.FromBounds(localDecl.Span.Start, ifStatement.Span.End);
            var originalCode = context.SyntaxTree.GetText(cancellationToken).GetSubText(replacementSpan).ToString();

            var updatedIf = ifStatement.WithCondition(SyntaxFactory.ParseExpression($"{asExpr.Left} is {typeSyntax} {varName}"))
                                       .NormalizeWhitespace()
                                       .WithLeadingTrivia(localDecl.GetLeadingTrivia())
                                       .WithTrailingTrivia(ifStatement.GetTrailingTrivia());

            results.Add(
                new DetectionResult
                {
                    RuleId = this.RuleId,
                    RuleName = this.RuleName,
                    Description = "Replace 'as' + null check with pattern matching",
                    FilePath = context.FilePath,
                    Span = replacementSpan,
                    LineSpan = context.SyntaxTree.GetLineSpan(replacementSpan, cancellationToken).Span,
                    OriginalCode = originalCode,
                    SuggestedCode = updatedIf.ToFullString(),
                    Severity = Severity.Suggestion,
                });
        }
    }

    private static string? TryFindIntroducedVariableName(StatementSyntax statement, string sourceExpression, string typeName)
    {
        if (statement is not BlockSyntax block) { return null; }

        foreach (var candidate in block.Statements.OfType<LocalDeclarationStatementSyntax>())
        {
            var variable = candidate.Declaration.Variables.FirstOrDefault();

            if (variable?.Initializer?.Value is not CastExpressionSyntax cast) { continue; }

            if (!cast.Type.ToString().Equals(typeName, StringComparison.Ordinal) ||
                !cast.Expression.ToString().Equals(sourceExpression, StringComparison.Ordinal)) { continue; }

            return variable.Identifier.Text;
        }

        return null;
    }

    private static StatementSyntax RewriteIsPatternBody(StatementSyntax statement, string sourceExpression, string typeName, string variableName)
    {
        var rewritten = (StatementSyntax)new MatchingCastRewriter(sourceExpression, typeName, variableName).Visit(statement);

        if (rewritten is not BlockSyntax block) { return rewritten; }

        var statements = block.Statements.Where(s => !IsCastIntroduction(s, sourceExpression, typeName)).ToList();

        return block.WithStatements(SyntaxFactory.List(statements));
    }

    private static bool IsCastIntroduction(StatementSyntax statement, string sourceExpression, string typeName)
    {
        if (statement is not LocalDeclarationStatementSyntax localDeclaration) { return false; }

        var variable = localDeclaration.Declaration.Variables.FirstOrDefault();

        return variable?.Initializer?.Value is CastExpressionSyntax cast &&
               cast.Type.ToString().Equals(typeName, StringComparison.Ordinal) &&
               cast.Expression.ToString().Equals(sourceExpression, StringComparison.Ordinal);
    }

    private static bool IsNullCheckForVariable(ExpressionSyntax condition, string variableName)
    {
        var text = condition.ToString();

        return text.Contains(variableName, StringComparison.Ordinal) && text.Contains("null", StringComparison.Ordinal);
    }

    private sealed class MatchingCastRewriter(string sourceExpression, string typeName, string variableName) : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitCastExpression(CastExpressionSyntax node)
        {
            if (node.Type.ToString().Equals(typeName, StringComparison.Ordinal) &&
                node.Expression.ToString().Equals(sourceExpression, StringComparison.Ordinal))
            {
                return SyntaxFactory.IdentifierName(variableName).WithTriviaFrom(node);
            }

            return base.VisitCastExpression(node);
        }
    }
}
