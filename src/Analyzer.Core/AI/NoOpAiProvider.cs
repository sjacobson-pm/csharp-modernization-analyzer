using Analyzer.Core.Detection;

namespace Analyzer.Core.AI;

/// <summary>
/// Fallback AI provider when AI is disabled or unavailable.
/// Returns template explanations based on rule documentation.
/// </summary>
public sealed class NoOpAiProvider : IAiProvider
{
    public Task<string> GenerateExplanationAsync(DetectionResult result, CancellationToken cancellationToken = default)
    {
        var explanation = $"Rule {result.RuleId} ({result.RuleName}): {result.Description}";
        return Task.FromResult(explanation);
    }

    public Task<string> RefineSuggestionAsync(DetectionResult result, string surroundingContext, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(result.SuggestedCode);
    }
}
