using Analyzer.Core.Configuration;
using Analyzer.Core.Detection;
using Analyzer.Core.Detection.Detectors;
using Analyzer.Core.Standards;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Analyzer.Core.Tests;

public class PatternMatchingDetectorTests
{
    private readonly PatternMatchingDetector _detector = new();

    [Fact]
    public async Task Detects_Is_With_Cast_Pattern()
    {
        var code = """
            using System;

            public class Processor
            {
                public void Process(object input)
                {
                    if (input is string)
                    {
                        var text = (string)input;
                        Console.WriteLine(text.Length);
                    }
                }
            }
            """;

        var results = await RunDetectorAsync(code);

        Assert.Single(results);
        Assert.Equal("MOD004", results[0].RuleId);
        Assert.Contains("pattern matching", results[0].Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Detects_As_With_Null_Check_Pattern()
    {
        var code = """
            using System;

            public class Processor
            {
                public void Process(object input)
                {
                    var text = input as string;
                    if (text != null)
                    {
                        Console.WriteLine(text.Length);
                    }
                }
            }
            """;

        var results = await RunDetectorAsync(code);

        Assert.Single(results);
        Assert.Equal("MOD004", results[0].RuleId);
        Assert.Contains("as", results[0].Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Ignores_When_EditorConfig_Disables_Pattern_Matching()
    {
        var code = """
            public class Processor
            {
                public void Process(object input)
                {
                    if (input is string)
                    {
                        var text = (string)input;
                    }
                }
            }
            """;

        var standards = new ResolvedStandards
        {
            EditorConfigPreferences = new Dictionary<string, string>
            {
                ["csharp_style_pattern_matching_over_is_with_cast_check"] = "false : none",
                ["csharp_style_pattern_matching_over_as_with_null_check"] = "false : none"
            }
        };

        var results = await RunDetectorAsync(code, standards);

        Assert.Empty(results);
    }

    [Fact]
    public void Has_Correct_Minimum_LangVersion()
    {
        Assert.Equal(new Version(7, 0), _detector.MinimumLangVersion);
    }

    private async Task<IReadOnlyList<DetectionResult>> RunDetectorAsync(
        string code,
        ResolvedStandards? standards = null)
    {
        var tree = CSharpSyntaxTree.ParseText(code, path: "Test.cs");

        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Console).Assembly.Location)
        };

        var compilation = CSharpCompilation.Create("TestAssembly",
            syntaxTrees: [tree],
            references: references);

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
