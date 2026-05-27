using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Analyzer.Core.Detection;

/// <summary>
/// Represents a single modernization opportunity detected in source code.
/// </summary>
public sealed class DetectionResult
{
    public required string RuleId { get; init; }
    public required string RuleName { get; init; }
    public required string Description { get; init; }
    public required string FilePath { get; init; }
    public required TextSpan Span { get; init; }
    public required LinePositionSpan LineSpan { get; init; }
    public required string OriginalCode { get; init; }
    public required string SuggestedCode { get; init; }
    public required Severity Severity { get; init; }
    public string? Explanation { get; init; }
}

public enum Severity
{
    Suggestion,
    Warning,
    Error
}
