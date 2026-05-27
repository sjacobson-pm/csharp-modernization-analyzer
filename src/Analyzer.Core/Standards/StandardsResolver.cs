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
    private static readonly HttpClient SharedHttpClient = new();
    private readonly HttpClient httpClient = httpClient ?? SharedHttpClient;

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

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(content, new System.Text.Json.JsonDocumentOptions
            {
                CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            var root = doc.RootElement;
            var documentPrivate = false;
            var sysUsingFirst = false;
            var usingPlacement = "outsideNamespace";

            if (root.TryGetProperty("settings", out var settingsElement))
            {
                if (settingsElement.TryGetProperty("documentationRules", out var docRules) &&
                    docRules.TryGetProperty("documentPrivateElements", out var docPrivateEl))
                {
                    documentPrivate = docPrivateEl.GetBoolean();
                }

                if (settingsElement.TryGetProperty("orderingRules", out var orderRules))
                {
                    if (orderRules.TryGetProperty("systemUsingDirectivesFirst", out var sysFirstEl))
                    {
                        sysUsingFirst = sysFirstEl.GetBoolean();
                    }

                    if (orderRules.TryGetProperty("usingDirectivesPlacement", out var placementEl))
                    {
                        usingPlacement = placementEl.GetString() ?? "outsideNamespace";
                    }
                }
            }

            return Task.FromResult<StyleCopSettings?>(new StyleCopSettings
            {
                DocumentPrivateElements = documentPrivate,
                SystemUsingDirectivesFirst = sysUsingFirst,
                UsingDirectivesPlacement = usingPlacement,
            });
        }
        catch
        {
            return Task.FromResult<StyleCopSettings?>(null);
        }
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
