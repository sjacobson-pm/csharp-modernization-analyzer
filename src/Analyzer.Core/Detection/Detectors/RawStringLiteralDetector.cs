using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Analyzer.Core.Detection.Detectors;

/// <summary>
/// MOD011: Detects string literals that would be clearer as raw string literals.
/// </summary>
public sealed class RawStringLiteralDetector : IPatternDetector
{
    public string RuleId => "MOD011";

    public string RuleName => "raw-string-literal";

    public Version MinimumLangVersion => new(11, 0);

    public Task<IReadOnlyList<DetectionResult>> DetectAsync(DetectionContext context, CancellationToken cancellationToken = default)
    {
        var results = new List<DetectionResult>();
        var root = context.SyntaxTree.GetRoot(cancellationToken);

        foreach (var literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>())
        {
            if (literal.RawKind != (int)Microsoft.CodeAnalysis.CSharp.SyntaxKind.StringLiteralExpression) { continue; }

            var token = literal.Token;
            var tokenText = token.Text;

            if (tokenText.StartsWith(
                    """"
                    """
                    """",
                    StringComparison.Ordinal)) { continue; }

            var isVerbatim = tokenText.StartsWith("@\"", StringComparison.Ordinal);
            var escapeCount = isVerbatim ? 0 : CountEscapeSequences(tokenText);
            var multilineVerbatim = isVerbatim && token.ValueText.Contains('\n');

            if (escapeCount < 3 && !multilineVerbatim) { continue; }

            var description = multilineVerbatim
                ? "Use a raw string literal for multi-line verbatim text"
                : "Use a raw string literal to reduce escape noise";

            results.Add(
                DetectorUtilities.CreateResult(
                    context,
                    this,
                    literal,
                    description,
                    DetectorUtilities.CreateRawStringLiteral(token.ValueText),
                    originalCode: tokenText));
        }

        return Task.FromResult<IReadOnlyList<DetectionResult>>(results);
    }

    private static int CountEscapeSequences(string tokenText)
    {
        var count = 0;

        for (var index = 1; index < tokenText.Length - 1; index++)
        {
            if (tokenText[index] != '\\') { continue; }

            count++;
            index++;
        }

        return count;
    }
}
