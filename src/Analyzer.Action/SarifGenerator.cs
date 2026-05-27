using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Analyzer.Core.Detection;

namespace Analyzer.Action;

public sealed class SarifGenerator
{
    public async Task<string> GenerateAsync(string repoRoot, IReadOnlyList<DetectionResult> results, CancellationToken cancellationToken = default)
    {
        var outputDirectory = Path.Combine(repoRoot, "artifacts");
        Directory.CreateDirectory(outputDirectory);

        var sarifPath = Path.Combine(outputDirectory, "modernization-report.sarif");

        var payload = new Dictionary<string, object?>
        {
            ["$schema"] = "https://json.schemastore.org/sarif-2.1.0.json",
            ["version"] = "2.1.0",
            ["runs"] = new object[]
            {
                new
                {
                    tool = new
                    {
                        driver = new { name = "C# Modernization Analyzer", semanticVersion = "1.0.0", rules = BuildRules(results) },
                    },
                    results = BuildResults(results),
                },
            },
        };

        await using var stream = File.Create(sarifPath);

        await JsonSerializer.SerializeAsync(
            stream,
            payload,
            new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull },
            cancellationToken);

        return sarifPath;
    }

    private static object[] BuildRules(IReadOnlyList<DetectionResult> results) =>
        results.GroupBy(result => new { result.RuleId, result.RuleName, result.Description })
               .OrderBy(group => group.Key.RuleId, StringComparer.OrdinalIgnoreCase)
               .Select(group => new
                {
                    id = group.Key.RuleId,
                    name = group.Key.RuleName,
                    shortDescription = new { text = group.Key.RuleName },
                    fullDescription = new { text = group.Key.Description },
                    defaultConfiguration = new { level = ToSarifLevel(group.Max(static result => result.Severity)) },
                })
               .Cast<object>()
               .ToArray();

    private static object[] BuildResults(IReadOnlyList<DetectionResult> results) =>
        results.OrderBy(result => result.FilePath, StringComparer.OrdinalIgnoreCase)
               .ThenBy(result => result.LineSpan.Start.Line)
               .Select(result => new
                {
                    ruleId = result.RuleId,
                    level = ToSarifLevel(result.Severity),
                    message = new { text = result.Description },
                    locations =
                        new object[]
                        {
                            new
                            {
                                physicalLocation = new
                                {
                                    artifactLocation = new { uri = result.FilePath.Replace('\\', '/') },
                                    region =
                                        new
                                        {
                                            startLine = result.LineSpan.Start.Line + 1,
                                            startColumn = result.LineSpan.Start.Character + 1,
                                            endLine = Math.Max(result.LineSpan.End.Line + 1, result.LineSpan.Start.Line + 1),
                                            endColumn =
                                                Math.Max(result.LineSpan.End.Character + 1, result.LineSpan.Start.Character + 1),
                                        },
                                },
                            },
                        },
                    fixes = new object[]
                    {
                        new
                        {
                            description = new { text = "Apply suggested modernization" },
                            artifactChanges = new object[]
                            {
                                new
                                {
                                    artifactLocation = new { uri = result.FilePath.Replace('\\', '/') },
                                    replacements = new object[]
                                    {
                                        new
                                        {
                                            deletedRegion = new
                                            {
                                                startLine = result.LineSpan.Start.Line + 1,
                                                startColumn = result.LineSpan.Start.Character + 1,
                                                endLine =
                                                    Math.Max(
                                                        result.LineSpan.End.Line + 1,
                                                        result.LineSpan.Start.Line + 1),
                                                endColumn =
                                                    Math.Max(
                                                        result.LineSpan.End.Character + 1,
                                                        result.LineSpan.Start.Character + 1),
                                            },
                                            insertedContent = new { text = result.SuggestedCode },
                                        },
                                    },
                                },
                            },
                        },
                    },
                    properties = new { filePath = result.FilePath, ruleName = result.RuleName, explanation = result.Explanation },
                })
               .Cast<object>()
               .ToArray();

    private static string ToSarifLevel(Severity severity) =>
        severity switch
        {
            Severity.Error => "error",
            Severity.Warning => "warning",
            _ => "note",
        };
}
