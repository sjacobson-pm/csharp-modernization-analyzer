using Analyzer.Core.Detection;

namespace Analyzer.Core.Patching;

/// <summary>
/// Generates unified diffs and patch files from detection results.
/// </summary>
public sealed class PatchGenerator
{
    /// <summary>Generate a unified diff string for a single detection result.</summary>
    public string GenerateUnifiedDiff(DetectionResult result)
    {
        var originalLines = result.OriginalCode.Split('\n');
        var suggestedLines = result.SuggestedCode.Split('\n');
        var startLine = result.LineSpan.Start.Line + 1;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"--- a/{result.FilePath}");
        sb.AppendLine($"+++ b/{result.FilePath}");
        sb.AppendLine($"@@ -{startLine},{originalLines.Length} +{startLine},{suggestedLines.Length} @@");

        foreach (var line in originalLines)
            sb.AppendLine($"-{line}");

        foreach (var line in suggestedLines)
            sb.AppendLine($"+{line}");

        return sb.ToString();
    }

    /// <summary>Generate a collection of patches from multiple detection results.</summary>
    public IReadOnlyList<Patch> GeneratePatches(IReadOnlyList<DetectionResult> results)
    {
        return results.Select(result => new Patch
        {
            RuleId = result.RuleId,
            FilePath = result.FilePath,
            StartLine = result.LineSpan.Start.Line + 1,
            EndLine = result.LineSpan.End.Line + 1,
            OriginalCode = result.OriginalCode,
            SuggestedCode = result.SuggestedCode,
            UnifiedDiff = GenerateUnifiedDiff(result),
            Description = result.Description,
            Explanation = result.Explanation
        }).ToList();
    }
}

public sealed class Patch
{
    public required string RuleId { get; init; }
    public required string FilePath { get; init; }
    public required int StartLine { get; init; }
    public required int EndLine { get; init; }
    public required string OriginalCode { get; init; }
    public required string SuggestedCode { get; init; }
    public required string UnifiedDiff { get; init; }
    public required string Description { get; init; }
    public string? Explanation { get; init; }
}
