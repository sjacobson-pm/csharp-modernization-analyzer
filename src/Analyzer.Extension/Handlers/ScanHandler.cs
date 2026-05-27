using System.Text;
using Analyzer.Core.Configuration;
using Analyzer.Core.Detection;
using Analyzer.Core.Detection.Detectors;
using Analyzer.Core.Standards;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Octokit;

namespace Analyzer.Extension.Handlers;

internal sealed class ScanHandler
{
    private const int MaxFilesToAnalyze = 25;
    private static readonly ProductHeaderValue ExtensionProduct = new("csharp-modernization-extension");

    private readonly IHttpClientFactory _httpClientFactory;

    public ScanHandler(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public Task<CopilotResponse> HandleAsync(ScanPrIntent intent, CopilotRequestContext requestContext, CancellationToken cancellationToken)
    {
        var repoName = requestContext.RepositoryFullName ?? "the current repository";
        var prLabel = requestContext.PullRequestNumber is int pullRequestNumber
            ? $"PR #{pullRequestNumber} in `{repoName}`"
            : $"the active PR in `{repoName}`";

        var markdown = $"""
            ## PR scan request recognized

            I understood your request as **scan this PR** for {prLabel}.

            This first Copilot Extension build has the webhook routing and SSE response flow in place, but PR-diff collection is still a placeholder until the GitHub App token flow and repository content retrieval are wired up.

            Today you can already use:
            - `scan src/Services/`
            - `scan src/Services/MyService.cs`
            - `explain MOD004`
            - `what rules are available`
            """;

        return Task.FromResult(new CopilotResponse(
            "Scanning...",
            "Analyzing files for modernization opportunities",
            markdown,
            []));
    }

    public Task<CopilotResponse> HandleAsync(ScanDirectoryIntent intent, CopilotRequestContext requestContext, CancellationToken cancellationToken)
    {
        return BuildScanResponseAsync(intent.DirectoryPath, "directory", cancellationToken);
    }

    public Task<CopilotResponse> HandleAsync(ScanFileIntent intent, CopilotRequestContext requestContext, CancellationToken cancellationToken)
    {
        var requestedPath = intent.FilePath ?? requestContext.ReferencedPaths.FirstOrDefault();
        return BuildScanResponseAsync(requestedPath, "file", cancellationToken);
    }

    private async Task<CopilotResponse> BuildScanResponseAsync(string? requestedPath, string scope, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            return new CopilotResponse(
                "Scanning...",
                "Analyzing files for modernization opportunities",
                """
                I recognized this as a scan request, but I do not have a file or directory path yet.

                Try one of these forms:
                - `scan src/Services/`
                - `scan src/Services/MyService.cs`
                - `what can be modernized in this file` (from a file-aware Copilot context)
                """,
                []);
        }

        var repoRoot = FindRepositoryRoot(Directory.GetCurrentDirectory());
        var resolvedPath = ResolveLocalPath(repoRoot, requestedPath);
        if (resolvedPath is null)
        {
            return BuildPlaceholderScanResponse(requestedPath, scope);
        }

        var localScanResult = await TryAnalyzeAsync(repoRoot, resolvedPath, cancellationToken);
        var references = localScanResult.Files
            .Select(file => new CopilotReference("file", ToReferencePath(repoRoot, file)))
            .ToList();

        return new CopilotResponse(
            "Scanning...",
            "Analyzing files for modernization opportunities",
            BuildScanMarkdown(scope, requestedPath, repoRoot, localScanResult),
            references);
    }

    private CopilotResponse BuildPlaceholderScanResponse(string requestedPath, string scope)
    {
        var markdown = $"""
            ## {scope} scan request recognized

            I understood your request as a **{scope} scan** for `{requestedPath}`.

            This initial extension version can run the analyzer when the requested files are available on disk next to the service, but webhook-based repository checkout is still a placeholder.

            Once repository content retrieval is wired up, the same intent routing will return modernization findings directly in Copilot Chat.
            """;

        var references = scope == "file"
            ? new List<CopilotReference> { new("file", requestedPath.Replace('\\', '/')) }
            : new List<CopilotReference>();

        return new CopilotResponse(
            "Scanning...",
            "Analyzing files for modernization opportunities",
            markdown,
            references);
    }

    private async Task<LocalScanResult> TryAnalyzeAsync(string repoRoot, string resolvedPath, CancellationToken cancellationToken)
    {
        try
        {
            _ = new GitHubClient(ExtensionProduct);

            var configPath = Path.Combine(repoRoot, ".modernization.yml");
            var config = ModernizationConfig.LoadFromFile(configPath);
            var files = DiscoverFiles(resolvedPath, config, out var note);
            if (files.Count == 0)
            {
                return new LocalScanResult(false, files, [], note ?? "No C# files matched the request.");
            }

            var standardsResolver = new StandardsResolver(_httpClientFactory.CreateClient());
            var standards = await standardsResolver.ResolveAsync(repoRoot, config, cancellationToken);

            var syntaxTrees = new List<SyntaxTree>(files.Count);
            foreach (var file in files)
            {
                var source = await File.ReadAllTextAsync(file, cancellationToken);
                syntaxTrees.Add(CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview), file));
            }

            var compilation = CSharpCompilation.Create(
                assemblyName: "Analyzer.Extension.Scan",
                syntaxTrees: syntaxTrees,
                references: BuildMetadataReferences(),
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var engine = new DetectionEngine(
                new IPatternDetector[]
                {
                    new VarUsageDetector(),
                    new NullConditionalDetector(),
                    new StringInterpolationDetector(),
                    new PatternMatchingDetector(),
                    new SwitchExpressionDetector(),
                    new UsingDeclarationDetector(),
                    new NullCoalescingAssignmentDetector(),
                    new FileScopedNamespaceDetector(),
                    new TargetTypedNewDetector(),
                    new CollectionExpressionDetector(),
                    new RawStringLiteralDetector(),
                    new PrimaryConstructorDetector()
                },
                standards,
                config);

            var detections = await engine.AnalyzeFilesAsync(files, compilation, cancellationToken);
            return new LocalScanResult(true, files, detections, note);
        }
        catch (Exception ex)
        {
            return new LocalScanResult(false, [], [], $"Roslyn analysis is not available yet in webhook mode: {ex.Message}");
        }
    }

    private static string BuildScanMarkdown(string scope, string requestedPath, string repoRoot, LocalScanResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"## {scope} scan results");
        builder.AppendLine();
        builder.AppendLine($"Target: `{requestedPath}`");
        builder.AppendLine();

        if (!result.Executed)
        {
            builder.AppendLine(result.Note ?? "Analysis is still a placeholder for this request.");
            builder.AppendLine();
            builder.AppendLine("The extension plumbing is in place; repository checkout/content fetching is the next integration step.");
            return builder.ToString().TrimEnd();
        }

        builder.AppendLine($"Analyzed **{result.Files.Count}** C# file(s).");
        if (!string.IsNullOrWhiteSpace(result.Note))
        {
            builder.AppendLine();
            builder.AppendLine($"> {result.Note}");
        }

        if (result.Detections.Count == 0)
        {
            builder.AppendLine();
            builder.AppendLine("No modernization opportunities were detected by the currently wired rules (`MOD004` and `MOD008`).");
            return builder.ToString().TrimEnd();
        }

        builder.AppendLine();
        builder.AppendLine($"Found **{result.Detections.Count}** modernization opportunit{(result.Detections.Count == 1 ? "y" : "ies")} across **{result.Detections.Select(detection => detection.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count()}** file(s).");
        builder.AppendLine();
        builder.AppendLine("### Findings");

        foreach (var detection in result.Detections
            .OrderBy(detection => detection.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(detection => detection.LineSpan.Start.Line)
            .Take(10))
        {
            var referencePath = ToReferencePath(repoRoot, detection.FilePath);
            var lineNumber = detection.LineSpan.Start.Line + 1;
            builder.AppendLine($"- **{detection.RuleId}** — {detection.Description}  ");
            builder.AppendLine($"  `{referencePath}:{lineNumber}`  ");
            builder.AppendLine($"  Suggestion: `{detection.SuggestedCode.Replace(Environment.NewLine, " ", StringComparison.Ordinal)}`");
        }

        builder.AppendLine();
        builder.AppendLine("### Rule summary");
        builder.AppendLine("| Rule | Hits |");
        builder.AppendLine("| --- | ---: |");

        foreach (var group in result.Detections.GroupBy(detection => detection.RuleId).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"| {group.Key} | {group.Count()} |");
        }

        return builder.ToString().TrimEnd();
    }

    private static List<string> DiscoverFiles(string path, ModernizationConfig config, out string? note)
    {
        note = null;

        if (File.Exists(path) && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return [path];
        }

        if (!Directory.Exists(path))
        {
            note = "The requested path was resolved locally, but it is not a C# file or directory.";
            return [];
        }

        var allFiles = Directory.GetFiles(path, "*.cs", SearchOption.AllDirectories)
            .Where(file => !config.IsExcluded(file))
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (allFiles.Count > MaxFilesToAnalyze)
        {
            note = $"Showing the first {MaxFilesToAnalyze} files out of {allFiles.Count} discovered C# files.";
            return allFiles.Take(MaxFilesToAnalyze).ToList();
        }

        return allFiles;
    }

    private static IReadOnlyList<MetadataReference> BuildMetadataReferences()
    {
        return new[]
            {
                typeof(object).Assembly,
                typeof(Console).Assembly,
                typeof(Enumerable).Assembly,
                typeof(List<>).Assembly,
                typeof(Task).Assembly
            }
            .Select(assembly => assembly.Location)
            .Where(static location => !string.IsNullOrWhiteSpace(location))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(location => MetadataReference.CreateFromFile(location))
            .ToList();
    }

    private static string? ResolveLocalPath(string repoRoot, string requestedPath)
    {
        var cleanedPath = requestedPath.Trim().Trim('"', '\'', '`');
        if (Path.IsPathRooted(cleanedPath))
        {
            return File.Exists(cleanedPath) || Directory.Exists(cleanedPath)
                ? Path.GetFullPath(cleanedPath)
                : null;
        }

        var normalizedPath = cleanedPath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var combined = Path.GetFullPath(Path.Combine(repoRoot, normalizedPath));
        return File.Exists(combined) || Directory.Exists(combined)
            ? combined
            : null;
    }

    private static string FindRepositoryRoot(string startPath)
    {
        var current = Path.GetFullPath(startPath);

        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current, ".git")))
            {
                return current;
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent;
        }

        return Path.GetFullPath(startPath);
    }

    private static string ToReferencePath(string repoRoot, string fullPath)
    {
        var relative = Path.GetRelativePath(repoRoot, fullPath);
        return relative.Replace('\\', '/');
    }

    private sealed record LocalScanResult(
        bool Executed,
        IReadOnlyList<string> Files,
        IReadOnlyList<DetectionResult> Detections,
        string? Note);
}
