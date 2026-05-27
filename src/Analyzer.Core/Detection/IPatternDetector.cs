using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Analyzer.Core.Detection;

/// <summary>
/// Interface for all modernization pattern detectors.
/// Each detector identifies one category of outdated C# construct.
/// </summary>
public interface IPatternDetector
{
    /// <summary>The rule ID (e.g., "MOD001")</summary>
    string RuleId { get; }

    /// <summary>Human-readable rule name</summary>
    string RuleName { get; }

    /// <summary>Minimum C# language version required for the modern equivalent</summary>
    Version MinimumLangVersion { get; }

    /// <summary>Detect modernization opportunities in the given syntax tree</summary>
    Task<IReadOnlyList<DetectionResult>> DetectAsync(DetectionContext context, CancellationToken cancellationToken = default);
}
