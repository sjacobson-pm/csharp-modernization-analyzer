using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Analyzer.Core.Configuration;
using Analyzer.Core.Detection;
using Analyzer.Core.Standards;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Analyzer.Cli;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("C# Modernization Analyzer - Identifies outdated C# constructs and suggests modern equivalents");

        var scanCommand = BuildScanCommand();
        var configCommand = BuildConfigCommand();
        var mcpServeCommand = BuildMcpServeCommand();

        rootCommand.Subcommands.Add(scanCommand);
        rootCommand.Subcommands.Add(configCommand);
        rootCommand.Subcommands.Add(mcpServeCommand);

        var parseResult = rootCommand.Parse(args);

        return await parseResult.InvokeAsync();
    }

    private static Command BuildScanCommand()
    {
        var pathArgument = new Argument<string>("path")
        {
            Description = "Path to scan (file, directory, or solution)",
            Arity = ArgumentArity.ZeroOrOne,
            DefaultValueFactory = _ => ".",
        };

        var fullOption = new Option<bool>("--full") { Description = "Scan the entire repository instead of just changed files" };

        var diffOption = new Option<bool>("--diff") { Description = "Only scan uncommitted changes" };

        var outputOption = new Option<string>("--output")
        {
            Description = "Output format: console, json, sarif, markdown", DefaultValueFactory = _ => "console",
        };

        var configOption = new Option<string>("--config")
        {
            Description = "Path to configuration file", DefaultValueFactory = _ => ".modernization.yml",
        };

        var severityOption = new Option<string>("--severity")
        {
            Description = "Minimum severity to report: suggestion, warning, error", DefaultValueFactory = _ => "suggestion",
        };

        var ruleOption = new Option<string[]>("--rule")
        {
            Description = "Only run specific rule(s) by ID (e.g., --rule MOD008 --rule MOD013)",
            Arity = ArgumentArity.ZeroOrMore,
        };

        var excludeRuleOption = new Option<string[]>("--exclude-rule")
        {
            Description = "Exclude specific rule(s) by ID (e.g., --exclude-rule MOD001)",
            Arity = ArgumentArity.ZeroOrMore,
        };

        var command = new Command("scan", "Scan files for modernization opportunities")
        {
            pathArgument,
            fullOption,
            diffOption,
            outputOption,
            configOption,
            severityOption,
            ruleOption,
            excludeRuleOption,
        };

        command.SetAction(async (parseResult, _) =>
        {
            var path = parseResult.GetValue(pathArgument) ?? ".";
            var full = parseResult.GetValue(fullOption);
            var diff = parseResult.GetValue(diffOption);
            var output = parseResult.GetValue(outputOption) ?? "console";
            var configPath = parseResult.GetValue(configOption) ?? ".modernization.yml";
            var severity = parseResult.GetValue(severityOption) ?? "suggestion";
            var includeRules = parseResult.GetValue(ruleOption);
            var excludeRules = parseResult.GetValue(excludeRuleOption);

            await ExecuteScanAsync(path, full, diff, output, configPath, severity, includeRules, excludeRules);
        });

        return command;
    }

    private static Command BuildConfigCommand()
    {
        var initCommand = new Command("init", "Generate a .modernization.yml template");

        initCommand.SetAction(async (_, _) => { await GenerateConfigTemplateAsync(); });

        var validateCommand = new Command("validate", "Validate current configuration");

        var configPathOption = new Option<string>("--path")
        {
            Description = "Path to configuration file", DefaultValueFactory = _ => ".modernization.yml",
        };

        validateCommand.Options.Add(configPathOption);

        validateCommand.SetAction(async (parseResult, _) =>
        {
            var path = parseResult.GetValue(configPathOption) ?? ".modernization.yml";
            await ValidateConfigAsync(path);
        });

        var command = new Command("config", "Configuration management") { initCommand, validateCommand };

        return command;
    }

    private static Command BuildMcpServeCommand()
    {
        var transportOption = new Option<string>("--transport") { Description = "Transport mode: stdio or http", DefaultValueFactory = _ => "stdio" };

        var portOption = new Option<int>("--port") { Description = "HTTP port (only used with --transport http)", DefaultValueFactory = _ => 3000 };

        var command = new Command("mcp-serve", "Start MCP server for AI agent integration") { transportOption, portOption };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var transport = parseResult.GetValue(transportOption) ?? "stdio";
            var port = parseResult.GetValue(portOption);

            if (transport != "stdio")
            {
                Console.Error.WriteLine($"Starting MCP server on {transport} transport (port {port})...");
            }

            var server = new Analyzer.Mcp.McpServer();
            await server.RunAsync(transport, port, cancellationToken);
        });

        return command;
    }

    private static async Task ExecuteScanAsync(string path, bool full, bool diff, string output, string configPath, string severity, string[]? includeRules, string[]? excludeRules)
    {
        Console.WriteLine("╔══════════════════════════════════════════════╗");
        Console.WriteLine("║   C# Modernization Analyzer                  ║");
        Console.WriteLine("╚══════════════════════════════════════════════╝");
        Console.WriteLine();

        // Load configuration
        var config = ModernizationConfig.LoadFromFile(configPath);
        Console.WriteLine($"  Config: {(File.Exists(configPath) ? configPath : "defaults")}");
        Console.WriteLine($"  Scope:  {(full ? "full-repo" : diff ? "uncommitted changes" : config.Scope)}");
        Console.WriteLine($"  Output: {output}");
        Console.WriteLine();

        // Resolve standards
        Console.WriteLine("  Resolving coding standards...");
        var resolver = new StandardsResolver();
        var repoRoot = FindRepoRoot(path);
        var standards = await resolver.ResolveAsync(repoRoot, config);
        Console.WriteLine($"    ✓ EditorConfig: {standards.EditorConfigPreferences.Count} preferences");
        Console.WriteLine($"    ✓ StyleCop: {(standards.StyleCop is not null ? "loaded" : "not found")}");
        Console.WriteLine($"    ✓ External standards: {standards.ExternalStandards.Count} sources");
        Console.WriteLine();

        // Discover files
        Console.WriteLine("  Discovering files...");
        var files = DiscoverFiles(path, config);
        Console.WriteLine($"    Found {files.Count} C# file(s) to analyze");
        Console.WriteLine();

        if (files.Count == 0)
        {
            Console.WriteLine("  No files to analyze. Done.");

            return;
        }

        // Run analysis
        Console.WriteLine("  Running analysis...");
        var allDetectors = CreateDetectors();

        // Apply --rule / --exclude-rule filters
        var detectors = allDetectors.AsEnumerable();

        if (includeRules is { Length: > 0 })
        {
            var includeSet = new HashSet<string>(includeRules, StringComparer.OrdinalIgnoreCase);
            detectors = detectors.Where(d => includeSet.Contains(d.RuleId));
        }

        if (excludeRules is { Length: > 0 })
        {
            var excludeSet = new HashSet<string>(excludeRules, StringComparer.OrdinalIgnoreCase);
            detectors = detectors.Where(d => !excludeSet.Contains(d.RuleId));
        }

        var activeDetectors = detectors.ToList();
        var engine = new DetectionEngine(activeDetectors, standards, config);

        // Parse files into a Roslyn compilation
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
        var syntaxTrees = new List<SyntaxTree>();

        foreach (var file in files)
        {
            try
            {
                var sourceText = await File.ReadAllTextAsync(file);
                var tree = CSharpSyntaxTree.ParseText(sourceText, parseOptions, path: file);
                syntaxTrees.Add(tree);
            }
            catch (Exception ex) { Console.WriteLine($"    ⚠ Could not read: {Path.GetFileName(file)} ({ex.Message})"); }
        }

        // Create compilation with basic framework references
        var references = GetFrameworkReferences();

        var compilation = CSharpCompilation.Create(
            "TargetAnalysis",
            syntaxTrees: syntaxTrees,
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        Console.WriteLine($"    Parsed {syntaxTrees.Count} file(s) into compilation");
        Console.WriteLine($"    Running {activeDetectors.Count} detectors...");
        Console.WriteLine();

        var results = await engine.AnalyzeFilesAsync(files.ToList(), compilation);

        // Filter by severity
        var minSeverity = severity.ToLowerInvariant() switch
        {
            "warning" => Severity.Warning,
            "error" => Severity.Error,
            _ => Severity.Suggestion,
        };

        var filteredResults = results.Where(r => r.Severity >= minSeverity).ToList();

        // Output results
        Console.WriteLine("  ─── Results ───────────────────────────────────");
        Console.WriteLine();

        if (filteredResults.Count == 0) { Console.WriteLine("  No modernization opportunities detected."); }
        else
        {
            switch (output.ToLowerInvariant())
            {
                case "json":
                    OutputJson(filteredResults);
                    break;
                case "sarif":
                    OutputSarif(filteredResults, path);
                    break;
                default:
                    OutputConsole(filteredResults);
                    break;
            }
        }

        Console.WriteLine();
        Console.WriteLine("  ─── Summary ───────────────────────────────────");
        Console.WriteLine($"  Files scanned:  {files.Count}");
        Console.WriteLine($"  Suggestions:    {filteredResults.Count(r => r.Severity == Severity.Suggestion)}");
        Console.WriteLine($"  Warnings:       {filteredResults.Count(r => r.Severity == Severity.Warning)}");
        Console.WriteLine($"  Total findings: {filteredResults.Count}");
        Console.WriteLine();
    }

    private static List<IPatternDetector> CreateDetectors() =>
        DetectorRegistry.CreateAll().ToList();

    private static List<MetadataReference> GetFrameworkReferences()
    {
        var references = new List<MetadataReference>();

        // Get the runtime directory to find framework assemblies
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;

        var essentialAssemblies = new[]
        {
            "System.Runtime.dll",
            "System.Collections.dll",
            "System.Linq.dll",
            "System.Console.dll",
            "System.Private.CoreLib.dll",
            "System.Runtime.Extensions.dll",
            "netstandard.dll",
            "System.dll",
            "System.Core.dll",
            "System.Threading.Tasks.dll",
            "System.IO.dll",
            "System.Net.Http.dll",
            "System.ComponentModel.dll",
            "System.ObjectModel.dll",
            "Microsoft.CSharp.dll",
        };

        foreach (var asm in essentialAssemblies)
        {
            var path = Path.Combine(runtimeDir, asm);

            if (File.Exists(path)) { references.Add(MetadataReference.CreateFromFile(path)); }
        }

        return references;
    }

    private static void OutputConsole(List<DetectionResult> results)
    {
        var grouped = results.GroupBy(r => r.FilePath).ToList();

        foreach (var fileGroup in grouped)
        {
            var relativePath = Path.GetRelativePath(Directory.GetCurrentDirectory(), fileGroup.Key);
            Console.WriteLine($"  📄 {relativePath}");

            foreach (var result in fileGroup.OrderBy(r => r.LineSpan.Start.Line))
            {
                var line = result.LineSpan.Start.Line + 1;

                var severityIcon = result.Severity switch
                {
                    Severity.Warning => "⚠",
                    Severity.Error => "✗",
                    _ => "💡",
                };

                Console.WriteLine($"     {severityIcon} Line {line}: [{result.RuleId}] {result.Description}");
                Console.WriteLine($"       - {result.OriginalCode.Split('\n')[0].Trim()}");
                Console.WriteLine($"       + {result.SuggestedCode.Split('\n')[0].Trim()}");
                Console.WriteLine();
            }
        }
    }

    private static void OutputJson(List<DetectionResult> results)
    {
        var jsonResults = results.Select(r => new
        {
            ruleId = r.RuleId,
            ruleName = r.RuleName,
            description = r.Description,
            filePath = r.FilePath,
            line = r.LineSpan.Start.Line + 1,
            column = r.LineSpan.Start.Character + 1,
            severity = r.Severity.ToString().ToLowerInvariant(),
            originalCode = r.OriginalCode,
            suggestedCode = r.SuggestedCode,
        });

        var json = JsonSerializer.Serialize(jsonResults, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(json);
    }

    private static void OutputSarif(List<DetectionResult> results, string scanPath)
    {
        var repoRoot = FindRepoRoot(scanPath);

        var rules = results
            .GroupBy(r => r.RuleId)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var first = g.First();
                return new
                {
                    id = first.RuleId,
                    name = first.RuleName,
                    shortDescription = new { text = first.RuleName },
                    fullDescription = new { text = first.Description },
                    defaultConfiguration = new { level = ToSarifLevel(first.Severity) },
                };
            })
            .ToArray();

        var sarifResults = results
            .OrderBy(r => r.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.LineSpan.Start.Line)
            .Select(r =>
            {
                var relativePath = Path.GetRelativePath(repoRoot, r.FilePath).Replace('\\', '/');
                return new
                {
                    ruleId = r.RuleId,
                    level = ToSarifLevel(r.Severity),
                    message = new { text = r.Description },
                    locations = new object[]
                    {
                        new
                        {
                            physicalLocation = new
                            {
                                artifactLocation = new { uri = relativePath },
                                region = new
                                {
                                    startLine = r.LineSpan.Start.Line + 1,
                                    startColumn = r.LineSpan.Start.Character + 1,
                                    endLine = Math.Max(r.LineSpan.End.Line + 1, r.LineSpan.Start.Line + 1),
                                    endColumn = Math.Max(r.LineSpan.End.Character + 1, r.LineSpan.Start.Character + 1),
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
                                    artifactLocation = new { uri = relativePath },
                                    replacements = new object[]
                                    {
                                        new
                                        {
                                            deletedRegion = new
                                            {
                                                startLine = r.LineSpan.Start.Line + 1,
                                                startColumn = r.LineSpan.Start.Character + 1,
                                                endLine = Math.Max(r.LineSpan.End.Line + 1, r.LineSpan.Start.Line + 1),
                                                endColumn = Math.Max(r.LineSpan.End.Character + 1, r.LineSpan.Start.Character + 1),
                                            },
                                            insertedContent = new { text = r.SuggestedCode },
                                        },
                                    },
                                },
                            },
                        },
                    },
                };
            })
            .ToArray();

        var sarif = new
        {
            schema = "https://json.schemastore.org/sarif-2.1.0.json",
            version = "2.1.0",
            runs = new object[]
            {
                new
                {
                    tool = new { driver = new { name = "C# Modernization Analyzer", semanticVersion = "1.0.0", rules } },
                    results = sarifResults,
                },
            },
        };

        var json = JsonSerializer.Serialize(sarif, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(json);
    }

    private static string ToSarifLevel(Severity severity) =>
        severity switch
        {
            Severity.Error => "error",
            Severity.Warning => "warning",
            _ => "note",
        };

    private static async Task GenerateConfigTemplateAsync()
    {
        var template = """
                       # .modernization.yml - C# Modernization Analyzer Configuration
                       # Place this file in your repository root.

                       version: 1

                       scan:
                         scope: changed-files          # changed-files | full-repo
                         include:
                           - "src/**/*.cs"
                         exclude:
                           - "**/Migrations/**"
                           - "**/Generated/**"
                           - "**/obj/**"
                           - "**/bin/**"

                       standards:
                         external:
                           - url: "https://your-org.github.io/coding-standards/"
                             cache-ttl: 24h

                       rules:
                         MOD008:                       # file-scoped namespaces
                           enabled: true
                           severity: suggestion
                         MOD012:                       # primary constructors
                           enabled: false              # disable if org not ready

                       ai:
                         enabled: true
                         provider: openai              # openai | azure-openai | none
                         model: gpt-4o
                         explain: true                 # generate human-readable explanations
                       """;

        var outputPath = Path.Combine(Directory.GetCurrentDirectory(), ".modernization.yml");

        if (File.Exists(outputPath))
        {
            Console.WriteLine($"  ⚠ File already exists: {outputPath}");
            Console.WriteLine("  Use --force to overwrite (not yet implemented).");

            return;
        }

        await File.WriteAllTextAsync(outputPath, template);
        Console.WriteLine($"  ✓ Created: {outputPath}");
        Console.WriteLine("  Edit the file to customize rules and settings for your project.");
    }

    private static async Task ValidateConfigAsync(string path)
    {
        Console.WriteLine($"  Validating: {path}");

        if (!File.Exists(path))
        {
            Console.WriteLine("  ✗ File not found. Run 'pm-modernize config init' to create one.");

            return;
        }

        try
        {
            var config = ModernizationConfig.LoadFromFile(path);
            Console.WriteLine("  ✓ Configuration is valid.");
            Console.WriteLine($"    Scope: {config.Scope}");
            Console.WriteLine($"    Include patterns: {config.IncludePatterns.Count}");
            Console.WriteLine($"    Exclude patterns: {config.ExcludePatterns.Count}");
            Console.WriteLine($"    Rule overrides: {config.RuleOverrides.Count}");
            Console.WriteLine($"    AI enabled: {config.Ai.Enabled}");
            Console.WriteLine($"    External standards: {config.ExternalStandardUrls.Count}");
        }
        catch (Exception ex) { Console.WriteLine($"  ✗ Invalid configuration: {ex.Message}"); }

        await Task.CompletedTask;
    }

    private static string FindRepoRoot(string startPath)
    {
        var dir = Path.GetFullPath(startPath);

        if (File.Exists(dir)) { dir = Path.GetDirectoryName(dir)!; }

        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, ".git"))) { return dir; }

            dir = Path.GetDirectoryName(dir);
        }

        return Path.GetFullPath(startPath);
    }

    private static List<string> DiscoverFiles(string path, ModernizationConfig config)
    {
        var resolvedPath = Path.GetFullPath(path);
        var files = new List<string>();

        if (File.Exists(resolvedPath) && resolvedPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) { files.Add(resolvedPath); }
        else if (Directory.Exists(resolvedPath))
        {
            var csFiles = Directory.GetFiles(resolvedPath, "*.cs", SearchOption.AllDirectories);
            files.AddRange(csFiles.Where(f => !config.IsExcluded(f)));
        }

        return files;
    }
}
