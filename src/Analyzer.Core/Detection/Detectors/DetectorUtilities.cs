using System.Text;
using Analyzer.Core.Standards;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Analyzer.Core.Detection.Detectors;

internal static class DetectorUtilities
{
    public static bool PrefersEnabled(ResolvedStandards standards, string key)
    {
        var value = standards.GetEditorConfigValue(key);
        return value is null || value.StartsWith("true", StringComparison.OrdinalIgnoreCase);
    }

    public static ExpressionSyntax Unwrap(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
            expression = parenthesized.Expression;

        return expression;
    }

    public static bool ExpressionsMatch(
        ExpressionSyntax left,
        ExpressionSyntax right,
        SemanticModel semanticModel,
        CancellationToken cancellationToken = default)
    {
        var unwrappedLeft = Unwrap(left);
        var unwrappedRight = Unwrap(right);
        var leftSymbol = semanticModel.GetSymbolInfo(unwrappedLeft, cancellationToken).Symbol;
        var rightSymbol = semanticModel.GetSymbolInfo(unwrappedRight, cancellationToken).Symbol;

        if (leftSymbol is not null && rightSymbol is not null)
            return SymbolEqualityComparer.Default.Equals(leftSymbol, rightSymbol);

        return string.Equals(unwrappedLeft.ToString(), unwrappedRight.ToString(), StringComparison.Ordinal);
    }

    public static ITypeSymbol? GetExpressionType(
        SemanticModel semanticModel,
        ExpressionSyntax expression,
        CancellationToken cancellationToken = default)
    {
        var typeInfo = semanticModel.GetTypeInfo(Unwrap(expression), cancellationToken);
        return typeInfo.ConvertedType ?? typeInfo.Type;
    }

    public static bool IsNullLiteral(ExpressionSyntax expression)
    {
        return Unwrap(expression).IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.NullLiteralExpression);
    }

    public static bool TryGetSingleStatement(StatementSyntax statement, out StatementSyntax singleStatement)
    {
        if (statement is BlockSyntax { Statements.Count: 1 } block)
        {
            singleStatement = block.Statements[0];
            return true;
        }

        if (statement is BlockSyntax)
        {
            singleStatement = null!;
            return false;
        }

        singleStatement = statement;
        return true;
    }

    public static bool TryGetSimpleAssignment(
        StatementSyntax statement,
        out AssignmentExpressionSyntax assignment)
    {
        assignment = null!;

        if (!TryGetSingleStatement(statement, out var singleStatement))
            return false;

        if (singleStatement is not ExpressionStatementSyntax
            {
                Expression: AssignmentExpressionSyntax { RawKind: (int)Microsoft.CodeAnalysis.CSharp.SyntaxKind.SimpleAssignmentExpression } simpleAssignment
            })
        {
            return false;
        }

        assignment = simpleAssignment;
        return true;
    }

    public static string IndentLines(string text, int spaces = 4)
    {
        var indent = new string(' ', spaces);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        return string.Join(Environment.NewLine, lines.Select(line => indent + line));
    }

    public static DetectionResult CreateResult(
        DetectionContext context,
        IPatternDetector detector,
        SyntaxNode node,
        string description,
        string suggestedCode,
        Severity severity = Severity.Suggestion,
        string? originalCode = null)
    {
        return new DetectionResult
        {
            RuleId = detector.RuleId,
            RuleName = detector.RuleName,
            Description = description,
            FilePath = context.FilePath,
            Span = node.Span,
            LineSpan = node.GetLocation().GetLineSpan().Span,
            OriginalCode = originalCode ?? node.ToString(),
            SuggestedCode = suggestedCode,
            Severity = severity
        };
    }

    public static string CreateRawStringLiteral(string value)
    {
        var delimiterLength = Math.Max(3, GetMaxQuoteRun(value) + 1);
        var delimiter = new string('"', delimiterLength);
        var normalizedValue = value.Replace("\r\n", "\n", StringComparison.Ordinal);

        if (normalizedValue.Contains('\n'))
            return $"{delimiter}{Environment.NewLine}{normalizedValue}{Environment.NewLine}{delimiter}";

        return $"{delimiter}{normalizedValue}{delimiter}";
    }

    private static int GetMaxQuoteRun(string value)
    {
        var currentRun = 0;
        var maxRun = 0;

        foreach (var ch in value)
        {
            if (ch == '"')
            {
                currentRun++;
                maxRun = Math.Max(maxRun, currentRun);
            }
            else
            {
                currentRun = 0;
            }
        }

        return maxRun;
    }
}
