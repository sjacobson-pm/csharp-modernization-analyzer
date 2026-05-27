using Analyzer.Core.Configuration;
using Analyzer.Core.Detection;
using Analyzer.Core.Detection.Detectors;
using Analyzer.Core.Standards;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Analyzer.Core.Tests;

public class FileScopedNamespaceDetectorTests
{
    private readonly FileScopedNamespaceDetector _detector = new();

    [Fact]
    public async Task Detects_Traditional_Namespace_Block()
    {
        var code = """
            using System;

            namespace TestApp.Services
            {
                public class MyService
                {
                    public void DoWork() { }
                }
            }
            """;

        var results = await RunDetectorAsync(code);

        Assert.Single(results);
        Assert.Equal("MOD008", results[0].RuleId);
        Assert.Contains("file-scoped namespace", results[0].Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Ignores_Multiple_Namespaces()
    {
        var code = """
            namespace TestApp.Services
            {
                public class ServiceA { }
            }

            namespace TestApp.Models
            {
                public class ModelA { }
            }
            """;

        var results = await RunDetectorAsync(code);

        Assert.Empty(results);
    }

    [Fact]
    public async Task Ignores_Nested_Namespaces()
    {
        var code = """
            namespace TestApp
            {
                namespace Services
                {
                    public class MyService { }
                }
            }
            """;

        var results = await RunDetectorAsync(code);

        Assert.Empty(results);
    }

    [Fact]
    public async Task Respects_EditorConfig_Block_Preference()
    {
        var code = """
            namespace TestApp.Services
            {
                public class MyService { }
            }
            """;

        var standards = new ResolvedStandards
        {
            EditorConfigPreferences = new Dictionary<string, string>
            {
                ["csharp_style_namespace_declarations"] = "block_scoped : warning"
            }
        };

        var results = await RunDetectorAsync(code, standards);

        Assert.Empty(results);
    }

    [Fact]
    public async Task Skipped_When_LangVersion_Below_10()
    {
        Assert.Equal(new Version(10, 0), _detector.MinimumLangVersion);
    }

    private async Task<IReadOnlyList<DetectionResult>> RunDetectorAsync(
        string code,
        ResolvedStandards? standards = null)
    {
        var tree = CSharpSyntaxTree.ParseText(code, path: "Test.cs");
        var compilation = CSharpCompilation.Create("TestAssembly",
            syntaxTrees: [tree],
            references: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);

        var semanticModel = compilation.GetSemanticModel(tree);

        var context = new DetectionContext
        {
            SyntaxTree = tree,
            SemanticModel = semanticModel,
            Compilation = compilation,
            TargetLangVersion = new Version(12, 0),
            Standards = standards ?? new ResolvedStandards(),
            FilePath = "Test.cs"
        };

        return await _detector.DetectAsync(context);
    }
}
