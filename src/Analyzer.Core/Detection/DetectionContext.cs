using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Analyzer.Core.Detection;

/// <summary>
/// Context provided to pattern detectors for analysis.
/// </summary>
public sealed class DetectionContext
{
    public required SyntaxTree SyntaxTree { get; init; }
    public required SemanticModel SemanticModel { get; init; }
    public required CSharpCompilation Compilation { get; init; }
    public required Version TargetLangVersion { get; init; }
    public required Standards.ResolvedStandards Standards { get; init; }
    public required string FilePath { get; init; }
}
