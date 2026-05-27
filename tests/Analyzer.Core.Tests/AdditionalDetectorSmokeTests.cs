using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Analyzer.Core.Detection;
using Analyzer.Core.Detection.Detectors;
using Analyzer.Core.Standards;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Analyzer.Core.Tests;

public class AdditionalDetectorSmokeTests
{
    [Fact]
    public async Task VarUsageDetector_Finds_Explicit_Local_Type()
    {
        const string Code = """
                            public class Sample
                            {
                                public void Run()
                                {
                                    int count = 42;
                                }
                            }
                            """;

        var standards = new ResolvedStandards
        {
            EditorConfigPreferences = new Dictionary<string, string> { ["csharp_style_var_for_built_in_types"] = "true:suggestion", },
        };

        var results = await RunDetectorAsync<VarUsageDetector>(Code, standards);

        Assert.Contains(results, result => result.RuleId == "MOD001");
    }

    [Fact]
    public async Task NullConditionalDetector_Finds_Null_Propagation_And_Coalesce()
    {
        const string Code = """
                            public class Sample
                            {
                                public string Run(string? input, string fallback)
                                {
                                    if (input != null)
                                    {
                                        input.ToUpper();
                                    }

                                    return input == null ? fallback : input;
                                }
                            }
                            """;

        var results = await RunDetectorAsync<NullConditionalDetector>(Code);

        Assert.Contains(results, result => result.Description.Contains("null-conditional", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(results, result => result.Description.Contains("null-coalescing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task StringInterpolationDetector_Finds_String_Format_Call()
    {
        const string Code = """
                            public class Sample
                            {
                                public string Run(string name) => string.Format("Hello {0}", name);
                            }
                            """;

        var results = await RunDetectorAsync<StringInterpolationDetector>(Code);

        Assert.Contains(results, result => result.RuleId == "MOD003");
    }

    [Fact]
    public async Task SwitchExpressionDetector_Finds_If_Else_Chain()
    {
        const string Code = """
                            public class Sample
                            {
                                public string Run(int value)
                                {
                                    if (value == 1)
                                    {
                                        return "one";
                                    }
                                    else if (value == 2)
                                    {
                                        return "two";
                                    }
                                    else if (value == 3)
                                    {
                                        return "three";
                                    }
                                    else
                                    {
                                        return "other";
                                    }
                                }
                            }
                            """;

        var results = await RunDetectorAsync<SwitchExpressionDetector>(Code);

        Assert.Contains(results, result => result.RuleId == "MOD005");
    }

    [Fact]
    public async Task UsingDeclarationDetector_Finds_Final_Using_Block()
    {
        const string Code = """
                            using System;
                            using System.IO;

                            public class Sample
                            {
                                public void Run()
                                {
                                    using (var stream = new MemoryStream())
                                    {
                                        Console.WriteLine(stream.Length);
                                    }
                                }
                            }
                            """;

        var results = await RunDetectorAsync<UsingDeclarationDetector>(Code);

        Assert.Contains(results, result => result.RuleId == "MOD006");
    }

    [Fact]
    public async Task NullCoalescingAssignmentDetector_Finds_If_Assignment()
    {
        const string Code = """
                            public class Sample
                            {
                                private object? _value;

                                public void Run(object fallback)
                                {
                                    if (_value == null)
                                    {
                                        _value = fallback;
                                    }
                                }
                            }
                            """;

        var results = await RunDetectorAsync<NullCoalescingAssignmentDetector>(Code);

        Assert.Contains(results, result => result.RuleId == "MOD007");
    }

    [Fact]
    public async Task TargetTypedNewDetector_Finds_Redundant_Type_On_Right()
    {
        const string Code = """
                            using System.Collections.Generic;

                            public class Sample
                            {
                                public void Run()
                                {
                                    List<int> items = new List<int>();
                                }
                            }
                            """;

        var results = await RunDetectorAsync<TargetTypedNewDetector>(Code);

        Assert.Contains(results, result => result.RuleId == "MOD009");
    }

    [Fact]
    public async Task CollectionExpressionDetector_Finds_Array_Initializer()
    {
        const string Code = """
                            public class Sample
                            {
                                public void Run()
                                {
                                    int[] values = new int[] { 1, 2, 3 };
                                }
                            }
                            """;

        var results = await RunDetectorAsync<CollectionExpressionDetector>(Code);

        Assert.Contains(results, result => result.RuleId == "MOD010");
    }

    [Fact]
    public async Task RawStringLiteralDetector_Finds_Escape_Heavy_String()
    {
        const string Code = """
                            public class Sample
                            {
                                public void Run()
                                {
                                    var path = "C:\\temp\\files\\test.txt";
                                }
                            }
                            """;

        var results = await RunDetectorAsync<RawStringLiteralDetector>(Code);

        Assert.Contains(results, result => result.RuleId == "MOD011");
    }

    [Fact]
    public async Task PrimaryConstructorDetector_Finds_Simple_Assignment_Constructor()
    {
        const string Code = """
                            public interface IService { }
                            public interface ILogger { }

                            public class Sample
                            {
                                private readonly IService _service;
                                private readonly ILogger _logger;

                                public Sample(IService service, ILogger logger)
                                {
                                    _service = service;
                                    _logger = logger;
                                }
                            }
                            """;

        var results = await RunDetectorAsync<PrimaryConstructorDetector>(Code);

        Assert.Contains(results, result => result.RuleId == "MOD012");
    }

    private static async Task<IReadOnlyList<DetectionResult>> RunDetectorAsync<TDetector>(string code, ResolvedStandards? standards = null)
        where TDetector : IPatternDetector, new()
    {
        var tree = CSharpSyntaxTree.ParseText(code, path: "Test.cs");

        var references = new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(List<>).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.IO.MemoryStream).Assembly.Location),
            }.DistinctBy(static reference => reference.Display)
             .ToArray();

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
            Standards = standards ?? new ResolvedStandards(),
            FilePath = "Test.cs",
        };

        return await new TDetector().DetectAsync(context);
    }
}
