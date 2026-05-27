using System;
using System.Linq;
using System.Text;

namespace Analyzer.Extension.Handlers;

internal sealed class ListRulesHandler
{
    public CopilotResponse Handle(ListRulesIntent intent)
    {
        var builder = new StringBuilder();
        builder.AppendLine("## Available modernization rules");
        builder.AppendLine();
        builder.AppendLine("| Rule | Category | Min C# | Description |");
        builder.AppendLine("| --- | --- | --- | --- |");

        foreach (var rule in ModernizationRuleCatalog.All.OrderBy(rule => rule.Id, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"| {rule.Id} | {rule.Category} | {rule.MinimumCSharpVersion} | {rule.Description} |");
        }

        builder.AppendLine();
        builder.AppendLine("Try `explain MOD004` for a deeper explanation with before/after examples.");

        return new("Listing rules", "Loading the available modernization rules", builder.ToString().TrimEnd(), []);
    }
}
