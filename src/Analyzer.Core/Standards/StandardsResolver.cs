using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Analyzer.Core.Standards;

/// <summary>
/// Resolves coding standards from all available sources for a target repository.
/// </summary>
public sealed class StandardsResolver(HttpClient? httpClient = null)
{
    private readonly HttpClient httpClient = httpClient ?? new HttpClient();

    /// <summary>
    /// Resolve all standards for the given repository root path.
    /// </summary>
    public async Task<ResolvedStandards> ResolveAsync(
        string repoRootPath,
        Configuration.ModernizationConfig config,
        CancellationToken cancellationToken = default)
    {
        var editorConfigPrefs = await ParseEditorConfigAsync(repoRootPath);
        var styleCop = await ParseStyleCopAsync(repoRootPath);
        var ruleSeverities = await ParseRuleSetsAsync();
        var externalStandards = await this.FetchExternalStandardsAsync(config, cancellationToken);

        return new ResolvedStandards
        {
            EditorConfigPreferences = editorConfigPrefs,
            StyleCop = styleCop,
            RuleSeverities = ruleSeverities,
            ExternalStandards = externalStandards,
        };
    }

    private static Task<Dictionary<string, string>> ParseEditorConfigAsync(string repoRoot)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var editorConfigPath = Path.Combine(repoRoot, ".editorconfig");

        if (!File.Exists(editorConfigPath)) { return Task.FromResult(result); }

        var lines = File.ReadAllLines(editorConfigPath);
        var inCSharpSection = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            if (trimmed.StartsWith('['))
            {
                inCSharpSection = trimmed.Contains("*.cs") || trimmed.Contains("*.{cs");

                continue;
            }

            if (!inCSharpSection || !trimmed.Contains('=')) { continue; }

            var parts = trimmed.Split('=', 2);

            if (parts.Length == 2) { result[parts[0].Trim()] = parts[1].Trim(); }
        }

        return Task.FromResult(result);
    }

    private static Task<StyleCopSettings?> ParseStyleCopAsync(string repoRoot)
    {
        var path = Path.Combine(repoRoot, "stylecop.json");

        if (!File.Exists(path)) { return Task.FromResult<StyleCopSettings?>(null); }

        var content = File.ReadAllText(path);

        var settings = new StyleCopSettings
        {
            DocumentPrivateElements = content.Contains("\"documentPrivateElements\": true"),
            SystemUsingDirectivesFirst = content.Contains("\"systemUsingDirectivesFirst\": true"),
            UsingDirectivesPlacement = content.Contains("\"outsideNamespace\"") ? "outsideNamespace" : "insideNamespace",
        };

        return Task.FromResult<StyleCopSettings?>(settings);
    }

    private static Task<Dictionary<string, string>> ParseRuleSetsAsync()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        return Task.FromResult(result);
    }

    private async Task<List<ExternalStandard>> FetchExternalStandardsAsync(Configuration.ModernizationConfig config, CancellationToken ct)
    {
        var standards = new List<ExternalStandard>();

        foreach (var url in config.ExternalStandardUrls)
        {
            try
            {
                var content = await this.httpClient.GetStringAsync(url, ct);
                standards.Add(new ExternalStandard { Url = url, Content = content, FetchedAt = DateTimeOffset.UtcNow });
            }
            catch (HttpRequestException) { }
        }

        return standards;
    }
}
