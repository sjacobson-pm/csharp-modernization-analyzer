using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Analyzer.Core.Detection;

/// <summary>
/// Orchestrates pattern detection across files/projects.
/// </summary>
public sealed class DetectionEngine
{
    private readonly IReadOnlyList<IPatternDetector> _detectors;
    private readonly Standards.ResolvedStandards _standards;
    private readonly Configuration.ModernizationConfig _config;

    public DetectionEngine(
        IReadOnlyList<IPatternDetector> detectors,
        Standards.ResolvedStandards standards,
        Configuration.ModernizationConfig config)
    {
        _detectors = detectors;
        _standards = standards;
        _config = config;
    }

    /// <summary>
    /// Analyze a list of file paths and return all detected modernization opportunities.
    /// </summary>
    public async Task<IReadOnlyList<DetectionResult>> AnalyzeFilesAsync(
        IReadOnlyList<string> filePaths,
        CSharpCompilation compilation,
        CancellationToken cancellationToken = default)
    {
        var results = new List<DetectionResult>();
        var langVersion = GetEffectiveLangVersion(compilation);

        foreach (var filePath in filePaths)
        {
            if (_config.IsExcluded(filePath))
                continue;

            var tree = compilation.SyntaxTrees
                .FirstOrDefault(t => t.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));

            if (tree is null)
                continue;

            var semanticModel = compilation.GetSemanticModel(tree);

            var context = new DetectionContext
            {
                SyntaxTree = tree,
                SemanticModel = semanticModel,
                Compilation = compilation,
                TargetLangVersion = langVersion,
                Standards = _standards,
                FilePath = filePath
            };

            foreach (var detector in _detectors)
            {
                if (detector.MinimumLangVersion > langVersion)
                    continue;

                if (!_config.IsRuleEnabled(detector.RuleId))
                    continue;

                var detections = await detector.DetectAsync(context, cancellationToken);
                results.AddRange(detections);
            }
        }

        return results;
    }

    private static Version GetEffectiveLangVersion(CSharpCompilation compilation)
    {
        var langVersion = compilation.LanguageVersion;
        return langVersion switch
        {
            LanguageVersion.CSharp1 => new Version(1, 0),
            LanguageVersion.CSharp2 => new Version(2, 0),
            LanguageVersion.CSharp3 => new Version(3, 0),
            LanguageVersion.CSharp4 => new Version(4, 0),
            LanguageVersion.CSharp5 => new Version(5, 0),
            LanguageVersion.CSharp6 => new Version(6, 0),
            LanguageVersion.CSharp7 => new Version(7, 0),
            LanguageVersion.CSharp7_1 => new Version(7, 1),
            LanguageVersion.CSharp7_2 => new Version(7, 2),
            LanguageVersion.CSharp7_3 => new Version(7, 3),
            LanguageVersion.CSharp8 => new Version(8, 0),
            LanguageVersion.CSharp9 => new Version(9, 0),
            LanguageVersion.CSharp10 => new Version(10, 0),
            LanguageVersion.CSharp11 => new Version(11, 0),
            LanguageVersion.CSharp12 => new Version(12, 0),
            _ => new Version(12, 0)
        };
    }
}
