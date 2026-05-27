using System.CommandLine;
using Analyzer.Core.Configuration;
using Analyzer.Core.Detection;
using Analyzer.Core.Standards;
using Analyzer.Core.Patching;
using Analyzer.Core.AI;

namespace Analyzer.Cli;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("C# Modernization Analyzer - Identifies outdated C# constructs and suggests modern equivalents");

        var scanCommand = BuildScanCommand();
        var configCommand = BuildConfigCommand();
        var mcpServeCommand = BuildMcpServeCommand();

        rootCommand.AddCommand(scanCommand);
        rootCommand.AddCommand(configCommand);
        rootCommand.AddCommand(mcpServeCommand);

        return await rootCommand.InvokeAsync(args);
    }

    private static Command BuildScanCommand()
    {
        var pathArgument = new Argument<string>("path", () => ".", "Path to scan (file, directory, or solution)");
        var fullOption = new Option<bool>("--full", "Scan the entire repository instead of just changed files");
        var diffOption = new Option<bool>("--diff", "Only scan uncommitted changes");
        var outputOption = new Option<string>("--output", () => "console", "Output format: console, json, sarif, markdown");
        var configOption = new Option<string>("--config", () => ".modernization.yml", "Path to configuration file");
        var severityOption = new Option<string>("--severity", () => "suggestion", "Minimum severity to report: suggestion, warning, error");

        var command = new Command("scan", "Scan files for modernization opportunities")
        {
            pathArgument,
            fullOption,
            diffOption,
            outputOption,
            configOption,
            severityOption
        };

        command.SetHandler(async (path, full, diff, output, configPath, severity) =>
        {
            await ExecuteScanAsync(path, full, diff, output, configPath, severity);
        }, pathArgument, fullOption, diffOption, outputOption, configOption, severityOption);

        return command;
    }

    private static Command BuildConfigCommand()
    {
        var initCommand = new Command("init", "Generate a .modernization.yml template");
        initCommand.SetHandler(async () =>
        {
            await GenerateConfigTemplateAsync();
        });

        var validateCommand = new Command("validate", "Validate current configuration");
        var configPathOption = new Option<string>("--path", () => ".modernization.yml", "Path to configuration file");
        validateCommand.AddOption(configPathOption);
        validateCommand.SetHandler(async (path) =>
        {
            await ValidateConfigAsync(path);
        }, configPathOption);

        var command = new Command("config", "Configuration management")
        {
            initCommand,
            validateCommand
        };

        return command;
    }

    private static Command BuildMcpServeCommand()
    {
        var transportOption = new Option<string>("--transport", () => "stdio", "Transport mode: stdio or http");
        var portOption = new Option<int>("--port", () => 3000, "HTTP port (only used with --transport http)");

        var command = new Command("mcp-serve", "Start MCP server for AI agent integration")
        {
            transportOption,
            portOption
        };

        command.SetHandler(async (transport, port) =>
        {
            Console.WriteLine($"Starting MCP server ({transport} transport)...");
            Console.WriteLine("MCP server implementation pending - see Analyzer.Mcp project");
            // TODO: Wire up to Analyzer.Mcp server
            await Task.CompletedTask;
        }, transportOption, portOption);

        return command;
    }

    private static async Task ExecuteScanAsync(string path, bool full, bool diff, string output, string configPath, string severity)
    {
        Console.WriteLine("╔══════════════════════════════════════════════╗");
        Console.WriteLine("║   C# Modernization Analyzer                 ║");
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
        var files = DiscoverFiles(path, full, diff, config);
        Console.WriteLine($"    Found {files.Count} C# file(s) to analyze");
        Console.WriteLine();

        if (files.Count == 0)
        {
            Console.WriteLine("  No files to analyze. Done.");
            return;
        }

        // Run analysis
        Console.WriteLine("  Running analysis...");
        Console.WriteLine("    (Roslyn compilation and detection engine)");
        Console.WriteLine();

        // TODO: Wire up actual Roslyn compilation and DetectionEngine
        // For now, show what the output format will look like
        Console.WriteLine("  ─── Results ───────────────────────────────────");
        Console.WriteLine();
        Console.WriteLine("  No modernization opportunities detected.");
        Console.WriteLine("  (Detection engine integration pending)");
        Console.WriteLine();
        Console.WriteLine("  ─── Summary ───────────────────────────────────");
        Console.WriteLine($"  Files scanned: {files.Count}");
        Console.WriteLine("  Suggestions:   0");
        Console.WriteLine("  Warnings:      0");
        Console.WriteLine();
    }

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
        catch (Exception ex)
        {
            Console.WriteLine($"  ✗ Invalid configuration: {ex.Message}");
        }

        await Task.CompletedTask;
    }

    private static string FindRepoRoot(string startPath)
    {
        var dir = Path.GetFullPath(startPath);
        if (File.Exists(dir))
            dir = Path.GetDirectoryName(dir)!;

        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, ".git")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }

        return Path.GetFullPath(startPath);
    }

    private static List<string> DiscoverFiles(string path, bool full, bool diff, ModernizationConfig config)
    {
        var resolvedPath = Path.GetFullPath(path);
        var files = new List<string>();

        if (File.Exists(resolvedPath) && resolvedPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            files.Add(resolvedPath);
        }
        else if (Directory.Exists(resolvedPath))
        {
            var csFiles = Directory.GetFiles(resolvedPath, "*.cs", SearchOption.AllDirectories);
            files.AddRange(csFiles.Where(f => !config.IsExcluded(f)));
        }

        return files;
    }
}
