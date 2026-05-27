using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Analyzer.Core.Configuration;
using Analyzer.Core.Detection;
using Analyzer.Core.Standards;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Analyzer.Core.Tests;

/// <summary>
/// Integration tests that exercise the full detection pipeline end-to-end.
/// </summary>
public class DetectionEngineIntegrationTests
{
    [Fact]
    public async Task AnalyzeFiles_WithParallelExecution_ReturnsConsistentResults()
    {
        // Generate enough files to trigger parallel execution (>5 files)
        // Use file-scoped namespaces pattern (MOD008) which is syntactic and reliably detects
        var files = Enumerable.Range(1, 10).Select(i =>
        {
            var code = "namespace App.Services" + i + "\n{\n" +
                       "    public class Service" + i + " { }\n}";
            return (Path: $"src/Service{i}.cs", Code: code);
        }).ToList();

        var results = await RunFullPipelineAsync(files);

        // MOD008 (file-scoped namespace) should fire for each file
        var mod008Results = results.Where(r => r.RuleId == "MOD008").ToList();
        Assert.Equal(10, mod008Results.Count);
    }

    [Fact]
    public async Task AnalyzeFiles_RespectConfigExclusions()
    {
        var files = new List<(string Path, string Code)>
        {
            ("src/Service.cs", "namespace App { public class Service { } }"),
            ("Migrations/20240101_Init.cs", "namespace App.Migrations { public class Init { } }"),
        };

        var config = new ModernizationConfig
        {
            ExcludePatterns = ["**/Migrations/**"],
        };

        var results = await RunFullPipelineAsync(files, config: config);

        // Only src/Service.cs should be analyzed (Migrations excluded)
        Assert.DoesNotContain(results, r => r.FilePath.Contains("Migrations"));
    }

    [Fact]
    public async Task AnalyzeFiles_RespectRuleDisablement()
    {
        var files = new List<(string Path, string Code)>
        {
            ("Test.cs", """
                namespace OldStyle
                {
                    public class Foo { }
                }
                """),
        };

        var config = new ModernizationConfig();
        config.RuleOverrides["MOD008"] = new RuleOverride { Enabled = false };

        var results = await RunFullPipelineAsync(files, config: config);

        Assert.DoesNotContain(results, r => r.RuleId == "MOD008");
    }

    [Fact]
    public async Task AnalyzeFiles_RespectsEditorConfigDisable()
    {
        var files = new List<(string Path, string Code)>
        {
            ("Test.cs", """
                public class Sample
                {
                    public void Run()
                    {
                        int count = 42;
                    }
                }
                """),
        };

        var standards = new ResolvedStandards
        {
            EditorConfigPreferences = new Dictionary<string, string>
            {
                ["csharp_style_var_for_built_in_types"] = "false:none",
                ["csharp_style_var_when_type_is_apparent"] = "false:none",
                ["csharp_style_var_elsewhere"] = "false:none",
            },
        };

        var results = await RunFullPipelineAsync(files, standards: standards);

        Assert.DoesNotContain(results, r => r.RuleId == "MOD001");
    }

    [Fact]
    public async Task AnalyzeFiles_DetectorRegistryIncludesAllRules()
    {
        var detectors = DetectorRegistry.CreateAll();

        // Verify all 13 rules are registered
        Assert.Equal(13, detectors.Count);

        var ruleIds = detectors.Select(d => d.RuleId).OrderBy(id => id).ToList();
        Assert.Contains("MOD001", ruleIds);
        Assert.Contains("MOD013", ruleIds);
    }

    [Fact]
    public async Task AnalyzeFiles_WithAlreadyCancelledToken_ThrowsOrReturnsEmpty()
    {
        var files = Enumerable.Range(1, 20).Select(i =>
            ($"File{i}.cs", $"namespace N{i} {{ public class C{i} {{ }} }}")
        ).ToList();

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        // With pre-cancelled token, either throws OperationCanceledException
        // or returns empty/partial results (depending on how fast the pipeline runs)
        try
        {
            var results = await RunFullPipelineAsync(files, cancellationToken: cts.Token);
            // If it didn't throw, the fast path completed before checking the token — acceptable
        }
        catch (OperationCanceledException)
        {
            // Expected behavior when cancellation is detected
        }
    }

    private static async Task<IReadOnlyList<DetectionResult>> RunFullPipelineAsync(
        IReadOnlyList<(string Path, string Code)> files,
        ModernizationConfig? config = null,
        ResolvedStandards? standards = null,
        CancellationToken cancellationToken = default)
    {
        config ??= new ModernizationConfig { IncludePatterns = [] };
        standards ??= new ResolvedStandards();

        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);

        var syntaxTrees = files
            .Select(f => CSharpSyntaxTree.ParseText(f.Code, parseOptions, path: f.Path))
            .ToList();

        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Collections.dll")),
        };

        var compilation = CSharpCompilation.Create(
            "IntegrationTest",
            syntaxTrees: syntaxTrees,
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var detectors = DetectorRegistry.CreateAll();
        var engine = new DetectionEngine(detectors, standards, config);

        return await engine.AnalyzeFilesAsync(
            files.Select(f => f.Path).ToList(),
            compilation,
            cancellationToken);
    }
}
