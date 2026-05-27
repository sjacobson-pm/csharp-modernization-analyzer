using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Analyzer.Core.Detection;
using Analyzer.Core.Detection.Detectors;
using Analyzer.Core.Standards;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Analyzer.Core.Tests;

public class ExplicitLambdaReturnTypeDetectorTests
{
    private readonly ExplicitLambdaReturnTypeDetector detector = new();

    [Fact]
    public async Task Detects_Cast_In_Simple_Lambda_Body()
    {
        const string Code = """
                            using System;
                            using System.Collections.Generic;
                            using System.Linq;

                            public class Example
                            {
                                public void Run(string[] paths)
                                {
                                    var items = paths.Select(path => (object)path.ToUpper());
                                }
                            }
                            """;

        var results = await this.RunDetectorAsync(Code);

        Assert.Single(results);
        Assert.Equal("MOD013", results[0].RuleId);
        Assert.Contains("object", results[0].SuggestedCode);
        Assert.Contains("(path)", results[0].SuggestedCode);
        Assert.DoesNotContain("(object)", results[0].SuggestedCode);
    }

    [Fact]
    public async Task Detects_Cast_In_Static_Lambda()
    {
        const string Code = """
                            using System;

                            public class Example
                            {
                                public void Run()
                                {
                                    Func<string, object> fn = static s => (object)s.ToUpper();
                                }
                            }
                            """;

        var results = await this.RunDetectorAsync(Code);

        Assert.Single(results);
        Assert.Contains("static", results[0].SuggestedCode);
        Assert.Contains("object (s)", results[0].SuggestedCode);
    }

    [Fact]
    public async Task Detects_Cast_In_Parenthesized_Lambda()
    {
        const string Code = """
                            using System;

                            public class Example
                            {
                                public void Run()
                                {
                                    Func<string, int, object> fn = (s, i) => (object)s.Substring(i);
                                }
                            }
                            """;

        var results = await this.RunDetectorAsync(Code);

        Assert.Single(results);
        Assert.Contains("object (s, i)", results[0].SuggestedCode);
    }

    [Fact]
    public async Task Ignores_Lambda_Without_Cast_Body()
    {
        const string Code = """
                            using System;

                            public class Example
                            {
                                public void Run()
                                {
                                    Func<int, int> fn = x => x + 1;
                                }
                            }
                            """;

        var results = await this.RunDetectorAsync(Code);

        Assert.Empty(results);
    }

    [Fact]
    public async Task Ignores_Async_Lambda()
    {
        const string Code = """
                            using System;
                            using System.Threading.Tasks;

                            public class Example
                            {
                                public void Run()
                                {
                                    Func<string, Task<object>> fn = async s => (object)await Task.FromResult(s);
                                }
                            }
                            """;

        var results = await this.RunDetectorAsync(Code);

        Assert.Empty(results);
    }

    [Fact]
    public async Task Ignores_Lambda_With_Existing_Return_Type()
    {
        const string Code = """
                            using System;

                            public class Example
                            {
                                public void Run()
                                {
                                    // Lambda already has explicit return type — no suggestion needed
                                    var fn = object (string s) => s.ToUpper();
                                }
                            }
                            """;

        var results = await this.RunDetectorAsync(Code);

        Assert.Empty(results);
    }

    [Fact]
    public void Has_Correct_Minimum_LangVersion() => Assert.Equal(new Version(10, 0), this.detector.MinimumLangVersion);

    private async Task<IReadOnlyList<DetectionResult>> RunDetectorAsync(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest), path: "Test.cs");

        var runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();

        var references = new MetadataReference[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Task).Assembly.Location),
            MetadataReference.CreateFromFile(System.IO.Path.Combine(runtimeDir, "System.Runtime.dll")),
        };

        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            syntaxTrees: [tree],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var semanticModel = compilation.GetSemanticModel(tree);

        var context = new DetectionContext
        {
            SyntaxTree = tree,
            SemanticModel = semanticModel,
            Compilation = compilation,
            TargetLangVersion = new Version(12, 0),
            Standards = new ResolvedStandards(),
            FilePath = "Test.cs",
        };

        return await this.detector.DetectAsync(context);
    }
}
