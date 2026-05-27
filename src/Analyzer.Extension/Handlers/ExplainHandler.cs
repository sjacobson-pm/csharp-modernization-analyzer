using System.Text;

namespace Analyzer.Extension.Handlers;

internal sealed class ExplainHandler
{
    public CopilotResponse Handle(ExplainRuleIntent intent)
    {
        if (!ModernizationRuleCatalog.TryGet(intent.RuleId, out var rule) || rule is null)
        {
            return new CopilotResponse(
                "Explaining rule",
                $"Looking up {intent.RuleId}",
                $"I couldn't find `{intent.RuleId}`. Try `what rules are available` to see the supported modernization rules.",
                []);
        }

        var builder = new StringBuilder();
        builder.AppendLine($"## {rule.Id} — {rule.Name}");
        builder.AppendLine();
        builder.AppendLine($"- Category: {rule.Category}");
        builder.AppendLine($"- Minimum C#: {rule.MinimumCSharpVersion}");
        builder.AppendLine();
        builder.AppendLine(rule.Description);

        if (!string.IsNullOrWhiteSpace(rule.BeforeExample))
        {
            builder.AppendLine();
            builder.AppendLine("### Before");
            builder.AppendLine("```csharp");
            builder.AppendLine(rule.BeforeExample.Trim());
            builder.AppendLine("```");
        }

        if (!string.IsNullOrWhiteSpace(rule.AfterExample))
        {
            builder.AppendLine();
            builder.AppendLine("### After");
            builder.AppendLine("```csharp");
            builder.AppendLine(rule.AfterExample.Trim());
            builder.AppendLine("```");
        }

        builder.AppendLine();
        builder.AppendLine("Ask me to `scan this PR` or `scan src/Services/` when you want this rule applied to real code.");

        var references = TryGetRuleReference(rule.Id);
        return new CopilotResponse(
            "Explaining rule",
            $"Summarizing {rule.Id}",
            builder.ToString().TrimEnd(),
            references);
    }

    private static IReadOnlyList<CopilotReference> TryGetRuleReference(string ruleId)
    {
        var docsPath = Path.Combine(Directory.GetCurrentDirectory(), "docs", "rules", $"{ruleId.ToUpperInvariant()}.md");
        if (!File.Exists(docsPath))
        {
            return [];
        }

        return [new CopilotReference("file", Path.Combine("docs", "rules", Path.GetFileName(docsPath)).Replace('\\', '/'))];
    }
}
