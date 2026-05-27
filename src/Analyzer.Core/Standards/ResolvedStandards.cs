using System;
using System.Collections.Generic;

namespace Analyzer.Core.Standards;

/// <summary>
/// Represents the fully resolved coding standards from all sources
/// (editorconfig, stylecop, rulesets, external URLs).
/// </summary>
public sealed class ResolvedStandards
{
    /// <summary>EditorConfig style preferences keyed by property name</summary>
    public IReadOnlyDictionary<string, string> EditorConfigPreferences { get; init; } = new Dictionary<string, string>();

    /// <summary>StyleCop settings (parsed from stylecop.json)</summary>
    public StyleCopSettings? StyleCop { get; init; }

    /// <summary>Analyzer rule severities from .ruleset/.globalconfig</summary>
    public IReadOnlyDictionary<string, string> RuleSeverities { get; init; } = new Dictionary<string, string>();

    /// <summary>External standards content (fetched and cached)</summary>
    public IReadOnlyList<ExternalStandard> ExternalStandards { get; init; } = [];

    /// <summary>
    /// Check if an editorconfig preference is set to a specific value.
    /// Returns null if the preference is not configured.
    /// </summary>
    public string? GetEditorConfigValue(string key) => this.EditorConfigPreferences.GetValueOrDefault(key);

    /// <summary>
    /// Returns true if the editorconfig explicitly disables a style
    /// (value is "false" with any severity).
    /// </summary>
    public bool IsStyleDisabled(string key)
    {
        var value = this.GetEditorConfigValue(key);

        return value is not null && value.StartsWith("false", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class StyleCopSettings
{
    public bool DocumentPrivateElements { get; init; }

    public bool SystemUsingDirectivesFirst { get; init; }

    public string UsingDirectivesPlacement { get; init; } = "outsideNamespace";
}

public sealed class ExternalStandard
{
    public required string Url { get; init; }

    public required string Content { get; init; }

    public required DateTimeOffset FetchedAt { get; init; }
}
