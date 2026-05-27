using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Analyzer.Core.Detection.Detectors;

/// <summary>
/// MOD008: Detects traditional namespace blocks that can be converted to file-scoped namespace declarations.
/// </summary>
public sealed class FileScopedNamespaceDetector : IPatternDetector
{
    public string RuleId => "MOD008";
    public string RuleName => "file-scoped-namespace";
    public Version MinimumLangVersion => new(10, 0);

    public Task<IReadOnlyList<DetectionResult>> DetectAsync(DetectionContext context, CancellationToken cancellationToken = default)
    {
        var results = new List<DetectionResult>();

        var nsStyle = context.Standards.GetEditorConfigValue("csharp_style_namespace_declarations");
        if (nsStyle is not null && nsStyle.StartsWith("block", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<IReadOnlyList<DetectionResult>>(results);

        var root = context.SyntaxTree.GetRoot(cancellationToken);
        var namespaceDeclarations = root.DescendantNodes().OfType<NamespaceDeclarationSyntax>().ToList();

        if (namespaceDeclarations.Count != 1)
            return Task.FromResult<IReadOnlyList<DetectionResult>>(results);

        var ns = namespaceDeclarations[0];

        if (ns.DescendantNodes().OfType<NamespaceDeclarationSyntax>().Any())
            return Task.FromResult<IReadOnlyList<DetectionResult>>(results);

        var innerContent = string.Concat(ns.Externs.Select(static node => node.ToFullString())) +
                           string.Concat(ns.Usings.Select(static node => node.ToFullString())) +
                           string.Concat(ns.Members.Select(static node => node.ToFullString()));
        var suggestedCode = $"namespace {ns.Name};{Environment.NewLine}{Environment.NewLine}{Outdent(innerContent).TrimStart('\r', '\n')}";

        results.Add(new DetectionResult
        {
            RuleId = RuleId,
            RuleName = RuleName,
            Description = "Convert to file-scoped namespace to reduce nesting",
            FilePath = context.FilePath,
            Span = ns.Span,
            LineSpan = ns.GetLocation().GetLineSpan().Span,
            OriginalCode = ns.ToFullString(),
            SuggestedCode = suggestedCode,
            Severity = Severity.Suggestion
        });

        return Task.FromResult<IReadOnlyList<DetectionResult>>(results);
    }

    private static string Outdent(string text)
    {
        return Regex.Replace(text, "(?m)^(    |\t)", string.Empty);
    }
}
