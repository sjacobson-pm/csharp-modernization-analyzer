using System.Collections.Generic;

namespace Analyzer.Core.Detection;

/// <summary>
/// Central registry of all available pattern detectors.
/// Use this instead of duplicating detector lists across delivery surfaces.
/// </summary>
public static class DetectorRegistry
{
    /// <summary>
    /// Creates a new list of all available pattern detectors.
    /// Each call returns fresh instances suitable for independent use.
    /// </summary>
    public static IReadOnlyList<IPatternDetector> CreateAll() =>
    [
        new Detectors.VarUsageDetector(),
        new Detectors.NullConditionalDetector(),
        new Detectors.StringInterpolationDetector(),
        new Detectors.PatternMatchingDetector(),
        new Detectors.SwitchExpressionDetector(),
        new Detectors.UsingDeclarationDetector(),
        new Detectors.NullCoalescingAssignmentDetector(),
        new Detectors.FileScopedNamespaceDetector(),
        new Detectors.TargetTypedNewDetector(),
        new Detectors.CollectionExpressionDetector(),
        new Detectors.RawStringLiteralDetector(),
        new Detectors.PrimaryConstructorDetector(),
        new Detectors.ExplicitLambdaReturnTypeDetector(),
    ];
}
