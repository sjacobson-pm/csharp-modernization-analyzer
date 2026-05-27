using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Analyzer.Core.Configuration;

/// <summary>
/// Represents the .modernization.yml configuration for a target repository.
/// </summary>
public sealed class ModernizationConfig
{
    public string Scope { get; set; } = "changed-files";
    public List<string> IncludePatterns { get; set; } = ["src/**/*.cs"];
    public List<string> ExcludePatterns { get; set; } = ["**/Migrations/**", "**/Generated/**"];
    public List<string> ExternalStandardUrls { get; set; } = [];
    public Dictionary<string, RuleOverride> RuleOverrides { get; set; } = new();
    public AiConfig Ai { get; set; } = new();

    /// <summary>Check if a file path should be excluded from analysis.</summary>
    public bool IsExcluded(string filePath)
    {
        var normalized = filePath.Replace('\\', '/');

        foreach (var pattern in ExcludePatterns)
        {
            if (MatchesGlob(normalized, pattern))
                return true;
        }

        if (IncludePatterns.Count > 0)
        {
            return !IncludePatterns.Any(p => MatchesGlob(normalized, p));
        }

        return false;
    }

    /// <summary>Check if a rule is enabled (default: true unless explicitly disabled).</summary>
    public bool IsRuleEnabled(string ruleId)
    {
        if (RuleOverrides.TryGetValue(ruleId, out var ruleOverride))
            return ruleOverride.Enabled;

        return true;
    }

    /// <summary>Get the configured severity for a rule, or null for default.</summary>
    public Detection.Severity? GetRuleSeverity(string ruleId)
    {
        if (RuleOverrides.TryGetValue(ruleId, out var ruleOverride) && ruleOverride.SeverityOverride is not null)
        {
            return Enum.TryParse<Detection.Severity>(ruleOverride.SeverityOverride, true, out var severity)
                ? severity
                : null;
        }

        return null;
    }

    /// <summary>Load configuration from a .modernization.yml file.</summary>
    public static ModernizationConfig LoadFromFile(string filePath)
    {
        if (!File.Exists(filePath))
            return new ModernizationConfig();

        var yaml = File.ReadAllText(filePath);
        return LoadFromYaml(yaml);
    }

    /// <summary>Load configuration from YAML content.</summary>
    public static ModernizationConfig LoadFromYaml(string yaml)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(HyphenatedNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        try
        {
            var raw = deserializer.Deserialize<YamlConfigRoot>(yaml);
            return MapFromYaml(raw);
        }
        catch
        {
            return new ModernizationConfig();
        }
    }

    private static ModernizationConfig MapFromYaml(YamlConfigRoot? root)
    {
        if (root is null)
            return new ModernizationConfig();

        var config = new ModernizationConfig
        {
            Scope = root.Scan?.Scope ?? "changed-files",
            IncludePatterns = root.Scan?.Include ?? ["src/**/*.cs"],
            ExcludePatterns = root.Scan?.Exclude ?? ["**/Migrations/**", "**/Generated/**"],
            ExternalStandardUrls = root.Standards?.External?
                .Select(static e => e.Url)
                .Where(static url => !string.IsNullOrWhiteSpace(url))
                .Select(static url => url!)
                .ToList() ?? [],
            Ai = new AiConfig
            {
                Enabled = root.Ai?.Enabled ?? true,
                Provider = root.Ai?.Provider ?? "openai",
                Model = root.Ai?.Model ?? "gpt-4o",
                Explain = root.Ai?.Explain ?? true
            }
        };

        if (root.Rules is not null)
        {
            foreach (var (ruleId, ruleConfig) in root.Rules)
            {
                config.RuleOverrides[ruleId] = new RuleOverride
                {
                    Enabled = ruleConfig.Enabled ?? true,
                    SeverityOverride = ruleConfig.Severity
                };
            }
        }

        return config;
    }

    private static bool MatchesGlob(string path, string pattern)
    {
        var normalizedPattern = pattern.Replace('\\', '/');
        var regexBody = System.Text.RegularExpressions.Regex.Escape(normalizedPattern)
            .Replace("\\*\\*", ".*")
            .Replace("\\*", "[^/]*");
        var regexPattern = normalizedPattern.StartsWith("**", StringComparison.Ordinal)
            ? $"^{regexBody}$"
            : $"^(?:.*?/)?{regexBody}$";

        return System.Text.RegularExpressions.Regex.IsMatch(
            path,
            regexPattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}

public sealed class RuleOverride
{
    public bool Enabled { get; set; } = true;
    public string? SeverityOverride { get; set; }
}

public sealed class AiConfig
{
    public bool Enabled { get; set; } = true;
    public string Provider { get; set; } = "openai";
    public string Model { get; set; } = "gpt-4o";
    public bool Explain { get; set; } = true;
}

internal sealed class YamlConfigRoot
{
    public YamlScanConfig? Scan { get; set; }
    public YamlStandardsConfig? Standards { get; set; }
    public Dictionary<string, YamlRuleConfig>? Rules { get; set; }
    public YamlAiConfig? Ai { get; set; }
}

internal sealed class YamlScanConfig
{
    public string? Scope { get; set; }
    public List<string>? Include { get; set; }
    public List<string>? Exclude { get; set; }
}

internal sealed class YamlStandardsConfig
{
    public List<YamlExternalSource>? External { get; set; }
}

internal sealed class YamlExternalSource
{
    public string? Url { get; set; }
    public string? CacheTtl { get; set; }
}

internal sealed class YamlRuleConfig
{
    public bool? Enabled { get; set; }
    public string? Severity { get; set; }
}

internal sealed class YamlAiConfig
{
    public bool? Enabled { get; set; }
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public bool? Explain { get; set; }
}
