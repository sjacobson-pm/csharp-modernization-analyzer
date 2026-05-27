using System.Threading;
using System.Threading.Tasks;
using Analyzer.Core.Detection;

namespace Analyzer.Core.AI;

/// <summary>
/// Fallback AI provider when AI is disabled or unavailable.
/// Returns template explanations based on rule documentation.
/// </summary>
public sealed class NoOpAiProvider : IAiProvider
{
    public Task<string> GenerateExplanationAsync(DetectionResult result, CancellationToken cancellationToken = default) =>
        Task.FromResult($"Rule {result.RuleId} ({result.RuleName}): {result.Description}");

    public Task<string> RefineSuggestionAsync(DetectionResult result, string surroundingContext, CancellationToken cancellationToken = default) =>
        Task.FromResult(result.SuggestedCode);
}
