using System.Threading;
using System.Threading.Tasks;
using Analyzer.Core.Detection;

namespace Analyzer.Core.AI;

/// <summary>
/// Interface for AI providers that enhance detection results with explanations.
/// </summary>
public interface IAiProvider
{
    /// <summary>Generate an explanation for why a modernization is beneficial.</summary>
    Task<string> GenerateExplanationAsync(DetectionResult result, CancellationToken cancellationToken = default);

    /// <summary>Refine a suggested code change using AI for better readability.</summary>
    Task<string> RefineSuggestionAsync(DetectionResult result, string surroundingContext, CancellationToken cancellationToken = default);
}
