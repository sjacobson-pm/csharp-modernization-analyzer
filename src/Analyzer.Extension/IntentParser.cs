using System.Text.RegularExpressions;

namespace Analyzer.Extension;

public sealed class IntentParser
{
    public CopilotIntent Parse(string? message)
    {
        var rawMessage = message?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(rawMessage))
        {
            return new UnknownIntent(rawMessage);
        }

        var normalized = Normalize(rawMessage);

        if (IsListRulesIntent(normalized))
        {
            return new ListRulesIntent(rawMessage);
        }

        var explainMatch = Regex.Match(normalized, @"\b(?:explain|describe|what does)\s+(mod\d{3})\b", RegexOptions.IgnoreCase);
        if (explainMatch.Success)
        {
            return new ExplainRuleIntent(rawMessage, explainMatch.Groups[1].Value.ToUpperInvariant());
        }

        if (Regex.IsMatch(normalized, @"\bscan\s+(?:this|the|current)?\s*pr\b", RegexOptions.IgnoreCase))
        {
            return new ScanPrIntent(rawMessage);
        }

        if (Regex.IsMatch(normalized, @"\bwhat can be modernized in this file\b", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(normalized, @"\bscan\s+(?:this|the)\s+file\b", RegexOptions.IgnoreCase))
        {
            return new ScanFileIntent(rawMessage, null);
        }

        var explicitPath = ExtractPath(rawMessage);
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            if (LooksLikeFilePath(explicitPath))
            {
                return new ScanFileIntent(rawMessage, explicitPath);
            }

            return new ScanDirectoryIntent(rawMessage, explicitPath);
        }

        return new UnknownIntent(rawMessage);
    }

    private static bool IsListRulesIntent(string normalized)
    {
        return normalized.Contains("what rules are available", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("list rules", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("show rules", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("available rules", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string message)
    {
        return Regex.Replace(message.Trim(), @"\s+", " ");
    }

    private static string? ExtractPath(string message)
    {
        var quotedMatch = Regex.Match(message, @"\bscan\s+['\""](?<path>[^'\""\r\n]+)['\""]", RegexOptions.IgnoreCase);
        if (quotedMatch.Success)
        {
            return quotedMatch.Groups["path"].Value.Trim();
        }

        var inlineMatch = Regex.Match(message, @"\bscan\s+(?<path>(?!this\b|the\b|current\b|pr\b)[^\r\n]+)", RegexOptions.IgnoreCase);
        if (!inlineMatch.Success)
        {
            return null;
        }

        var path = inlineMatch.Groups["path"].Value
            .Trim()
            .TrimEnd('.', '?', '!', ';', ':');

        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    private static bool LooksLikeFilePath(string path)
    {
        return path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".csx", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase);
    }
}

public abstract record CopilotIntent(string RawMessage);

public sealed record ScanPrIntent(string RawMessage) : CopilotIntent(RawMessage);

public sealed record ScanDirectoryIntent(string RawMessage, string DirectoryPath) : CopilotIntent(RawMessage);

public sealed record ScanFileIntent(string RawMessage, string? FilePath) : CopilotIntent(RawMessage);

public sealed record ExplainRuleIntent(string RawMessage, string RuleId) : CopilotIntent(RawMessage);

public sealed record ListRulesIntent(string RawMessage) : CopilotIntent(RawMessage);

public sealed record UnknownIntent(string RawMessage) : CopilotIntent(RawMessage);
