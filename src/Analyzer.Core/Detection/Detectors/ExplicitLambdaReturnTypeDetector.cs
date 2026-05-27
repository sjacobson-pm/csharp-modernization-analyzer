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
/// MOD013: Detects lambda expressions whose body is a cast expression and suggests
/// using C# 10's explicit lambda return type syntax instead.
///
/// Before: static path => (MetadataReference)MetadataReference.CreateFromFile(path)
/// After:  static MetadataReference (path) => MetadataReference.CreateFromFile(path)
/// </summary>
public sealed class ExplicitLambdaReturnTypeDetector : IPatternDetector
{
    public string RuleId => "MOD013";

    public string RuleName => "explicit-lambda-return-type";

    public Version MinimumLangVersion => new(10, 0);

    public Task<IReadOnlyList<DetectionResult>> DetectAsync(DetectionContext context, CancellationToken cancellationToken = default)
    {
        var results = new List<DetectionResult>();
        var root = context.SyntaxTree.GetRoot(cancellationToken);

        foreach (var lambda in root.DescendantNodes().OfType<LambdaExpressionSyntax>())
        {
            // Skip lambdas that already have an explicit return type
            if (lambda is ParenthesizedLambdaExpressionSyntax { ReturnType: not null }) { continue; }

            // Skip async lambdas — the return type semantics differ (Task<T> wrapping)
            if (lambda.Modifiers.Any(SyntaxKind.AsyncKeyword)) { continue; }

            // Only match lambdas whose entire expression body is a cast
            if (lambda.ExpressionBody is not CastExpressionSyntax castExpr) { continue; }

            var castType = castExpr.Type;
            var innerExpression = castExpr.Expression;

            var suggested = RewriteLambda(lambda, castType, innerExpression);

            if (suggested is null) { continue; }

            results.Add(
                new DetectionResult
                {
                    RuleId = this.RuleId,
                    RuleName = this.RuleName,
                    Description = $"Use explicit lambda return type '{castType}' instead of cast expression",
                    FilePath = context.FilePath,
                    Span = lambda.Span,
                    LineSpan = lambda.GetLocation().GetLineSpan().Span,
                    OriginalCode = lambda.ToFullString(),
                    SuggestedCode = suggested,
                    Severity = Severity.Suggestion,
                });
        }

        return Task.FromResult<IReadOnlyList<DetectionResult>>(results);
    }

    private static string? RewriteLambda(LambdaExpressionSyntax lambda, TypeSyntax returnType, ExpressionSyntax newBody)
    {
        // Collect modifiers (static, etc.)
        var modifiers = lambda.Modifiers;
        var modifierText = modifiers.Any() ? string.Join(" ", modifiers.Select(m => m.Text)) + " " : "";

        string parameterList;

        switch (lambda)
        {
            case SimpleLambdaExpressionSyntax simple:
                // simple: path => ... → ReturnType (path) => ...
                parameterList = $"({simple.Parameter})";

                break;

            case ParenthesizedLambdaExpressionSyntax parens:
                // parens: (path, index) => ... → ReturnType (path, index) => ...
                parameterList = parens.ParameterList.ToString();

                break;

            default:
                return null;
        }

        return $"{modifierText}{returnType} {parameterList} => {newBody}";
    }
}
