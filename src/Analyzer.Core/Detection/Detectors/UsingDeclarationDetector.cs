using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Analyzer.Core.Detection.Detectors;

/// <summary>
/// MOD006: Detects using blocks that can be converted to using declarations.
/// </summary>
public sealed class UsingDeclarationDetector : IPatternDetector
{
    public string RuleId => "MOD006";

    public string RuleName => "using-declaration";

    public Version MinimumLangVersion => new(8, 0);

    public Task<IReadOnlyList<DetectionResult>> DetectAsync(DetectionContext context, CancellationToken cancellationToken = default)
    {
        var results = new List<DetectionResult>();
        var root = context.SyntaxTree.GetRoot(cancellationToken);

        foreach (var usingStatement in root.DescendantNodes().OfType<UsingStatementSyntax>())
        {
            if (usingStatement.Declaration is null || usingStatement.Expression is not null) { continue; }

            if (usingStatement.Parent is not BlockSyntax parentBlock) { continue; }

            if (parentBlock.Statements.LastOrDefault() != usingStatement) { continue; }

            if (usingStatement.Statement is not BlockSyntax bodyBlock) { continue; }

            var prefix = usingStatement.AwaitKeyword.RawKind == (int)SyntaxKind.AwaitKeyword ? "await " : string.Empty;
            var bodyText = string.Join(Environment.NewLine, bodyBlock.Statements.Select(statement => statement.ToString()));
            var separator = string.IsNullOrWhiteSpace(bodyText) ? string.Empty : Environment.NewLine;
            var suggestedCode = $"{prefix}using {usingStatement.Declaration};{separator}{bodyText}";

            results.Add(
                DetectorUtilities.CreateResult(
                    context,
                    this,
                    usingStatement,
                    "Use a using declaration when the disposable scope already extends to the end of the block",
                    suggestedCode));
        }

        return Task.FromResult<IReadOnlyList<DetectionResult>>(results);
    }
}
