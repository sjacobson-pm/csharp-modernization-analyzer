using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Analyzer.Core.AI;
using Analyzer.Core.Configuration;
using Analyzer.Core.Detection;
using Analyzer.Core.Detection.Detectors;
using Analyzer.Core.Standards;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Analyzer.Action;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = ActionOptions.Parse(args);
            var repoRoot = ResolveRepositoryRoot();
            var configPath = ResolveConfigPath(repoRoot, options.ConfigPath);
            var config = ModernizationConfig.LoadFromFile(configPath);
            var scope = string.IsNullOrWhiteSpace(options.Scope) ? config.Scope : options.Scope!;
            var severityThreshold = options.SeverityThreshold ?? Severity.Suggestion;
            var maxSuggestions = options.MaxSuggestions ?? 25;
            var aiEnabled = options.AiEnabled ?? config.Ai.Enabled;
            var aiProviderName = string.IsNullOrWhiteSpace(options.AiProvider) ? config.Ai.Provider : options.AiProvider!;
            var aiModel = string.IsNullOrWhiteSpace(options.AiModel) ? config.Ai.Model : options.AiModel!;
            var githubContext = GitHubActionContext.Load(repoRoot);

            if (string.IsNullOrWhiteSpace(githubContext.HeadSha))
            {
                githubContext = githubContext with { HeadSha = await GetHeadShaAsync(repoRoot) };
            }

            Console.WriteLine($"Repository root: {repoRoot}");
            Console.WriteLine($"Configuration: {(File.Exists(configPath) ? configPath : "defaults")}");
            Console.WriteLine($"Scope: {scope}");
            Console.WriteLine($"Severity threshold: {severityThreshold}");
            Console.WriteLine($"Max suggestions: {maxSuggestions}");

            var targetFiles = await DetermineTargetFilesAsync(repoRoot, scope, config, githubContext);
            Console.WriteLine($"Target files: {targetFiles.Count}");

            var standardsResolver = new StandardsResolver();
            var standards = await standardsResolver.ResolveAsync(repoRoot, config);
            Console.WriteLine($"Resolved {standards.EditorConfigPreferences.Count} EditorConfig preference(s) and {standards.ExternalStandards.Count} external standard source(s).");

            var compilation = BuildCompilation(repoRoot, targetFiles);
            var detectionEngine = new DetectionEngine(
                [
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
                ],
                standards,
                config);

            var detections = await detectionEngine.AnalyzeFilesAsync(targetFiles, compilation);
            var filteredResults = detections
                .Select(result => ApplySeverityOverride(result, config))
                .Where(result => MeetsSeverityThreshold(result.Severity, severityThreshold))
                .OrderBy(result => result.FilePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(result => result.LineSpan.Start.Line)
                .ToList();

            Console.WriteLine($"Findings after filtering: {filteredResults.Count}");

            var aiProvider = CreateAiProvider(aiEnabled, aiProviderName, aiModel);
            var reportableResults = await EnrichResultsAsync(
                repoRoot,
                filteredResults.Take(maxSuggestions).ToList(),
                aiProvider,
                config.Ai.Explain);

            var sarifGenerator = new SarifGenerator();
            var sarifPath = await sarifGenerator.GenerateAsync(repoRoot, filteredResults);

            var reporter = new GitHubReporter(githubContext, maxSuggestions);
            var postedSuggestions = await reporter.ReportAsync(reportableResults, filteredResults);

            GitHubActionsOutput.Set("suggestions-count", filteredResults.Count.ToString(CultureInfo.InvariantCulture));
            GitHubActionsOutput.Set("sarif-path", sarifPath);

            Console.WriteLine($"Posted inline suggestions: {postedSuggestions}");
            Console.WriteLine($"SARIF report: {sarifPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Analyzer action failed: {ex.Message}");
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static async Task<IReadOnlyList<DetectionResult>> EnrichResultsAsync(
        string repoRoot,
        IReadOnlyList<DetectionResult> results,
        IAiProvider aiProvider,
        bool includeExplanation,
        CancellationToken cancellationToken = default)
    {
        var enriched = new List<DetectionResult>(results.Count);

        foreach (var result in results)
        {
            var surroundingContext = await LoadSurroundingContextAsync(repoRoot, result, cancellationToken);
            var refinedSuggestion = await aiProvider.RefineSuggestionAsync(result, surroundingContext, cancellationToken);
            var explanation = includeExplanation
                ? await aiProvider.GenerateExplanationAsync(result, cancellationToken)
                : result.Explanation;

            enriched.Add(CloneResult(result, result.Severity, refinedSuggestion, explanation));
        }

        return enriched;
    }

    private static IAiProvider CreateAiProvider(bool aiEnabled, string provider, string model)
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (!aiEnabled || !string.Equals(provider, "openai", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(apiKey))
        {
            return new NoOpAiProvider();
        }

        return new OpenAiProvider(apiKey, model);
    }

    private static DetectionResult ApplySeverityOverride(DetectionResult result, ModernizationConfig config)
    {
        var overriddenSeverity = config.GetRuleSeverity(result.RuleId);
        return overriddenSeverity is null
            ? result
            : CloneResult(result, overriddenSeverity.Value, result.SuggestedCode, result.Explanation);
    }

    private static DetectionResult CloneResult(DetectionResult result, Severity severity, string suggestedCode, string? explanation)
    {
        return new DetectionResult
        {
            RuleId = result.RuleId,
            RuleName = result.RuleName,
            Description = result.Description,
            FilePath = result.FilePath,
            Span = result.Span,
            LineSpan = result.LineSpan,
            OriginalCode = result.OriginalCode,
            SuggestedCode = suggestedCode,
            Severity = severity,
            Explanation = explanation
        };
    }

    private static bool MeetsSeverityThreshold(Severity severity, Severity threshold)
    {
        return GetSeverityRank(severity) >= GetSeverityRank(threshold);
    }

    private static int GetSeverityRank(Severity severity)
    {
        return severity switch
        {
            Severity.Suggestion => 0,
            Severity.Warning => 1,
            Severity.Error => 2,
            _ => 0
        };
    }

    private static async Task<IReadOnlyList<string>> DetermineTargetFilesAsync(
        string repoRoot,
        string scope,
        ModernizationConfig config,
        GitHubActionContext githubContext,
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(scope, "full-repo", StringComparison.OrdinalIgnoreCase))
        {
            return DiscoverRepositoryFiles(repoRoot, config);
        }

        return await DiscoverChangedFilesAsync(repoRoot, githubContext, config, cancellationToken);
    }

    private static List<string> DiscoverRepositoryFiles(string repoRoot, ModernizationConfig config)
    {
        return Directory
            .EnumerateFiles(repoRoot, "*.cs", SearchOption.AllDirectories)
            .Select(file => NormalizeRelativePath(repoRoot, file))
            .Where(path => !config.IsExcluded(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<List<string>> DiscoverChangedFilesAsync(
        string repoRoot,
        GitHubActionContext githubContext,
        ModernizationConfig config,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(githubContext.BaseSha))
        {
            Console.WriteLine("PR base SHA unavailable; falling back to configured repository scan.");
            return [];
        }

        await EnsureBaseCommitAvailableAsync(repoRoot, githubContext, cancellationToken);

        var head = string.IsNullOrWhiteSpace(githubContext.HeadSha) ? "HEAD" : githubContext.HeadSha!;
        var diff = await RunProcessAsync(
            "git",
            $"diff --name-only --diff-filter=ACMRT {githubContext.BaseSha}...{head} --",
            repoRoot,
            cancellationToken,
            throwOnFailure: false);

        if (diff.ExitCode != 0)
        {
            Console.WriteLine("Unable to determine changed files from git diff; falling back to configured repository scan.");
            return [];
        }

        return diff.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Select(NormalizeRelativePath)
            .Where(path => !config.IsExcluded(path))
            .Where(path => File.Exists(Path.Combine(repoRoot, path.Replace('/', Path.DirectorySeparatorChar))))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task EnsureBaseCommitAvailableAsync(
        string repoRoot,
        GitHubActionContext githubContext,
        CancellationToken cancellationToken)
    {
        var baseSha = githubContext.BaseSha;
        if (string.IsNullOrWhiteSpace(baseSha))
        {
            return;
        }

        var exists = await RunProcessAsync(
            "git",
            $"cat-file -e {baseSha}^{{commit}}",
            repoRoot,
            cancellationToken,
            throwOnFailure: false);

        if (exists.ExitCode == 0)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(githubContext.BaseRef))
        {
            await RunProcessAsync(
                "git",
                $"fetch --no-tags --depth=1 origin {githubContext.BaseRef}",
                repoRoot,
                cancellationToken,
                throwOnFailure: false);
        }
        else
        {
            await RunProcessAsync(
                "git",
                $"fetch --no-tags --depth=1 origin {baseSha}",
                repoRoot,
                cancellationToken,
                throwOnFailure: false);
        }
    }

    private static CSharpCompilation BuildCompilation(string repoRoot, IReadOnlyList<string> targetFiles)
    {
        var parseOptions = new CSharpParseOptions(languageVersion: LanguageVersion.Preview);
        var syntaxTrees = new List<SyntaxTree>(targetFiles.Count);

        foreach (var relativePath in targetFiles)
        {
            var absolutePath = Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var content = File.ReadAllText(absolutePath, Encoding.UTF8);
            syntaxTrees.Add(CSharpSyntaxTree.ParseText(content, parseOptions, path: relativePath, encoding: Encoding.UTF8));
        }

        var compilationOptions = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);
        return CSharpCompilation.Create("ModernizationAnalyzerAction", syntaxTrees, GetMetadataReferences(), compilationOptions);
    }

    private static IEnumerable<MetadataReference> GetMetadataReferences()
    {
        var trustedPlatformAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (!string.IsNullOrWhiteSpace(trustedPlatformAssemblies))
        {
            return trustedPlatformAssemblies
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
                .ToList();
        }

        return
        [
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location)
        ];
    }

    private static async Task<string> LoadSurroundingContextAsync(string repoRoot, DetectionResult result, CancellationToken cancellationToken)
    {
        var absolutePath = Path.Combine(repoRoot, result.FilePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(absolutePath))
        {
            return result.OriginalCode;
        }

        var lines = await File.ReadAllLinesAsync(absolutePath, cancellationToken);
        if (lines.Length == 0)
        {
            return result.OriginalCode;
        }

        var startLine = Math.Max(0, result.LineSpan.Start.Line - 2);
        var endLine = Math.Min(lines.Length - 1, Math.Max(result.LineSpan.End.Line, result.LineSpan.Start.Line) + 2);
        return string.Join(Environment.NewLine, lines.Skip(startLine).Take(endLine - startLine + 1));
    }

    private static string ResolveRepositoryRoot()
    {
        var githubWorkspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        if (!string.IsNullOrWhiteSpace(githubWorkspace) && Directory.Exists(githubWorkspace))
        {
            return Path.GetFullPath(githubWorkspace);
        }

        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")) || File.Exists(Path.Combine(current.FullName, "CSharpModernizer.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return Directory.GetCurrentDirectory();
    }

    private static string ResolveConfigPath(string repoRoot, string? configPath)
    {
        var candidate = string.IsNullOrWhiteSpace(configPath) ? ".modernization.yml" : configPath;
        return Path.IsPathRooted(candidate)
            ? candidate
            : Path.GetFullPath(Path.Combine(repoRoot, candidate));
    }

    private static string NormalizeRelativePath(string repoRoot, string filePath)
    {
        var relative = Path.GetRelativePath(repoRoot, filePath);
        return NormalizeRelativePath(relative);
    }

    private static string NormalizeRelativePath(string filePath)
    {
        return filePath.Replace('\\', '/').TrimStart('.').TrimStart('/');
    }

    private static async Task<string?> GetHeadShaAsync(string repoRoot)
    {
        var result = await RunProcessAsync("git", "rev-parse HEAD", repoRoot, CancellationToken.None, throwOnFailure: false);
        return result.ExitCode == 0 ? result.StandardOutput.Trim() : null;
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        CancellationToken cancellationToken,
        bool throwOnFailure = true)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(cancellationToken);

        var result = new ProcessResult(process.ExitCode, await stdoutTask, await stderrTask);
        if (throwOnFailure && result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Command '{fileName} {arguments}' failed with exit code {result.ExitCode}: {result.StandardError}");
        }

        return result;
    }
}

internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

internal sealed record GitHubActionContext(
    string? Token,
    string? Owner,
    string? Repo,
    int? PullRequestNumber,
    string? BaseSha,
    string? BaseRef,
    string? HeadSha)
{
    public bool CanPostToPullRequest =>
        !string.IsNullOrWhiteSpace(Token) &&
        !string.IsNullOrWhiteSpace(Owner) &&
        !string.IsNullOrWhiteSpace(Repo) &&
        PullRequestNumber.HasValue;

    public static GitHubActionContext Load(string repoRoot)
    {
        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        var repository = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY");
        var eventPath = Environment.GetEnvironmentVariable("GITHUB_EVENT_PATH");
        string? owner = null;
        string? repo = null;
        int? pullRequestNumber = null;
        string? baseSha = null;
        string? baseRef = null;
        string? headSha = null;

        if (!string.IsNullOrWhiteSpace(repository))
        {
            var parts = repository.Split('/', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2)
            {
                owner = parts[0];
                repo = parts[1];
            }
        }

        if (!string.IsNullOrWhiteSpace(eventPath))
        {
            var resolvedEventPath = Path.IsPathRooted(eventPath)
                ? eventPath
                : Path.GetFullPath(Path.Combine(repoRoot, eventPath));

            if (File.Exists(resolvedEventPath))
            {
                using var stream = File.OpenRead(resolvedEventPath);
                using var json = JsonDocument.Parse(stream);
                var root = json.RootElement;

                if (root.TryGetProperty("number", out var numberElement) && numberElement.TryGetInt32(out var number))
                {
                    pullRequestNumber = number;
                }

                if (root.TryGetProperty("pull_request", out var pullRequest))
                {
                    if (pullRequestNumber is null &&
                        pullRequest.TryGetProperty("number", out var prNumberElement) &&
                        prNumberElement.TryGetInt32(out var prNumber))
                    {
                        pullRequestNumber = prNumber;
                    }

                    if (pullRequest.TryGetProperty("base", out var baseElement))
                    {
                        if (baseElement.TryGetProperty("sha", out var baseShaElement))
                        {
                            baseSha = baseShaElement.GetString();
                        }

                        if (baseElement.TryGetProperty("ref", out var baseRefElement))
                        {
                            baseRef = baseRefElement.GetString();
                        }
                    }

                    if (pullRequest.TryGetProperty("head", out var headElement) &&
                        headElement.TryGetProperty("sha", out var headShaElement))
                    {
                        headSha = headShaElement.GetString();
                    }
                }
            }
        }

        return new GitHubActionContext(token, owner, repo, pullRequestNumber, baseSha, baseRef, headSha);
    }
}

internal sealed class ActionOptions
{
    public string? Scope { get; init; }
    public string? ConfigPath { get; init; }
    public bool? AiEnabled { get; init; }
    public string? AiProvider { get; init; }
    public string? AiModel { get; init; }
    public Severity? SeverityThreshold { get; init; }
    public int? MaxSuggestions { get; init; }

    public static ActionOptions Parse(string[] args)
    {
        var argValues = ParseArgs(args);

        return new ActionOptions
        {
            Scope = ReadSetting(argValues, "scope", "INPUT_SCOPE"),
            ConfigPath = ReadSetting(argValues, "config", "config-path", "INPUT_CONFIG_PATH"),
            AiEnabled = ParseBoolean(ReadSetting(argValues, "ai-enabled", "INPUT_AI_ENABLED")),
            AiProvider = ReadSetting(argValues, "ai-provider", "INPUT_AI_PROVIDER"),
            AiModel = ReadSetting(argValues, "ai-model", "INPUT_AI_MODEL"),
            SeverityThreshold = ParseSeverity(ReadSetting(argValues, "severity-threshold", "INPUT_SEVERITY_THRESHOLD")),
            MaxSuggestions = ParsePositiveInt(ReadSetting(argValues, "max-suggestions", "INPUT_MAX_SUGGESTIONS"))
        };
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var key = arg[2..];
            var value = index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++index]
                : "true";
            values[key] = value;
        }

        return values;
    }

    private static string? ReadSetting(IReadOnlyDictionary<string, string> args, params string[] names)
    {
        foreach (var name in names)
        {
            if (args.TryGetValue(name, out var argValue) && !string.IsNullOrWhiteSpace(argValue))
            {
                return argValue;
            }

            var environmentValue = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(environmentValue))
            {
                return environmentValue;
            }
        }

        return null;
    }

    private static bool? ParseBoolean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "y" => true,
            "0" or "false" or "no" or "n" => false,
            _ => null
        };
    }

    private static Severity? ParseSeverity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Enum.TryParse<Severity>(value, ignoreCase: true, out var severity)
            ? severity
            : null;
    }

    private static int? ParsePositiveInt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : null;
    }
}

internal static class GitHubActionsOutput
{
    public static void Set(string key, string value)
    {
        var outputPath = Environment.GetEnvironmentVariable("GITHUB_OUTPUT");
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return;
        }

        File.AppendAllText(outputPath, $"{key}={value}{Environment.NewLine}");
    }
}
