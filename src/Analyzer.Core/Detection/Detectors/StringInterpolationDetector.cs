using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Analyzer.Core.Detection.Detectors;

/// <summary>
/// MOD003: Detects string.Format calls that can be replaced with string interpolation.
/// </summary>
public sealed class StringInterpolationDetector : IPatternDetector
{
    public string RuleId => "MOD003";
    public string RuleName => "string-interpolation";
    public Version MinimumLangVersion => new(6, 0);

    public Task<IReadOnlyList<DetectionResult>> DetectAsync(DetectionContext context, CancellationToken cancellationToken = default)
    {
        var results = new List<DetectionResult>();
        var root = context.SyntaxTree.GetRoot(cancellationToken);

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var symbol = context.SemanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol as IMethodSymbol;
            if (symbol is null || symbol.Name != nameof(string.Format))
                continue;

            if (symbol.ContainingType.SpecialType != SpecialType.System_String)
                continue;

            if (invocation.ArgumentList.Arguments.Count < 2)
                continue;

            var formatArgumentIndex = symbol.Parameters.Length > 0
                && symbol.Parameters[0].Type.SpecialType == SpecialType.System_String
                ? 0
                : -1;

            if (formatArgumentIndex != 0)
                continue;

            var formatArgument = invocation.ArgumentList.Arguments[0].Expression;
            var constantValue = context.SemanticModel.GetConstantValue(formatArgument, cancellationToken);
            if (!constantValue.HasValue || constantValue.Value is not string formatText)
                continue;

            if (formatText.Contains('\r') || formatText.Contains('\n'))
                continue;

            if (!TryCreateInterpolatedString(formatText, invocation.ArgumentList.Arguments.Skip(1).ToList(), out var suggestedCode))
                continue;

            results.Add(DetectorUtilities.CreateResult(
                context,
                this,
                invocation,
                "Use string interpolation instead of string.Format",
                suggestedCode));
        }

        return Task.FromResult<IReadOnlyList<DetectionResult>>(results);
    }

    private static bool TryCreateInterpolatedString(string formatText, IReadOnlyList<ArgumentSyntax> arguments, out string interpolated)
    {
        interpolated = string.Empty;
        var builder = new StringBuilder();
        var foundPlaceholder = false;

        for (var index = 0; index < formatText.Length; index++)
        {
            var current = formatText[index];

            if (current == '{')
            {
                if (index + 1 < formatText.Length && formatText[index + 1] == '{')
                {
                    builder.Append("{{");
                    index++;
                    continue;
                }

                var closingBrace = formatText.IndexOf('}', index + 1);
                if (closingBrace < 0)
                    return false;

                var placeholder = formatText.Substring(index + 1, closingBrace - index - 1);
                if (!TryParsePlaceholder(placeholder, arguments, out var replacement))
                    return false;

                builder.Append('{').Append(replacement).Append('}');
                foundPlaceholder = true;
                index = closingBrace;
                continue;
            }

            if (current == '}')
            {
                if (index + 1 < formatText.Length && formatText[index + 1] == '}')
                {
                    builder.Append("}}");
                    index++;
                    continue;
                }

                return false;
            }

            if (current == '"')
            {
                builder.Append("\\\"");
                continue;
            }

            if (current == '\\')
            {
                builder.Append("\\\\");
                continue;
            }

            builder.Append(current);
        }

        if (!foundPlaceholder)
            return false;

        interpolated = $"$\"{builder}\"";
        return true;
    }

    private static bool TryParsePlaceholder(string placeholder, IReadOnlyList<ArgumentSyntax> arguments, out string replacement)
    {
        replacement = string.Empty;
        placeholder = placeholder.Trim();
        var digitsLength = 0;

        while (digitsLength < placeholder.Length && char.IsDigit(placeholder[digitsLength]))
            digitsLength++;

        if (digitsLength == 0)
            return false;

        if (!int.TryParse(placeholder[..digitsLength], out var argumentIndex))
            return false;

        if (argumentIndex < 0 || argumentIndex >= arguments.Count)
            return false;

        replacement = arguments[argumentIndex].Expression.ToString() + placeholder[digitsLength..];
        return true;
    }
}
