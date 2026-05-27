using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Analyzer.Core.Configuration;
using Analyzer.Core.Detection;
using Analyzer.Core.Detection.Detectors;
using Analyzer.Core.Patching;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Analyzer.Mcp;

public sealed class McpServer
{
    private const string ProtocolVersion = "2024-11-05";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly IReadOnlyDictionary<string, IMcpTool> _tools;

    public McpServer()
    {
        var analyzer = new AnalyzerService();
        _tools = new IMcpTool[]
        {
            new Tools.ScanFilesTool(analyzer),
            new Tools.ScanDiffTool(analyzer),
            new Tools.GetSuggestionTool(analyzer),
            new Tools.ApplySuggestionTool(analyzer),
            new Tools.ListRulesTool(analyzer),
            new Tools.GetStandardsTool(analyzer)
        }.ToDictionary(static tool => tool.Name, StringComparer.OrdinalIgnoreCase);
    }

    public Task RunAsync(string transport, int port, CancellationToken cancellationToken)
    {
        return transport.ToLowerInvariant() switch
        {
            "stdio" => RunStdioAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(), cancellationToken),
            "http" or "sse" => RunHttpAsync(port, cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported transport '{transport}'. Expected 'stdio' or 'http'.")
        };
    }

    public async Task RunStdioAsync(Stream input, Stream output, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            string? requestJson;

            try
            {
                requestJson = await ReadMessageAsync(input, cancellationToken);
            }
            catch (Exception ex)
            {
                var parseError = SerializeErrorResponse(default(JsonElement?), -32700, ex.Message);
                await WriteMessageAsync(output, parseError, cancellationToken);
                continue;
            }

            if (requestJson is null)
            {
                break;
            }

            var responseJson = await HandleRequestAsync(requestJson, cancellationToken);
            if (responseJson is null)
            {
                continue;
            }

            await WriteMessageAsync(output, responseJson, cancellationToken);
        }
    }

    public async Task RunHttpAsync(int port, CancellationToken cancellationToken)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var contextTask = listener.GetContextAsync();
                var completed = await Task.WhenAny(contextTask, Task.Delay(Timeout.Infinite, cancellationToken));
                if (completed != contextTask)
                {
                    break;
                }

                _ = HandleHttpContextAsync(await contextTask, cancellationToken);
            }
        }
        finally
        {
            listener.Stop();
            listener.Close();
        }
    }

    private async Task HandleHttpContextAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            if (context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                context.Request.Url?.AbsolutePath.Equals("/sse", StringComparison.OrdinalIgnoreCase) == true)
            {
                context.Response.StatusCode = (int)HttpStatusCode.OK;
                context.Response.ContentType = "text/event-stream";
                context.Response.Headers["Cache-Control"] = "no-cache";

                await using var writer = new StreamWriter(context.Response.OutputStream, new UTF8Encoding(false), leaveOpen: true);
                await writer.WriteAsync("event: endpoint\n");
                await writer.WriteAsync("data: /mcp\n\n");
                await writer.FlushAsync();

                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
                    await writer.WriteAsync(": keep-alive\n\n");
                    await writer.FlushAsync();
                }

                return;
            }

            if (!context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) ||
                context.Request.Url?.AbsolutePath.Equals("/mcp", StringComparison.OrdinalIgnoreCase) != true)
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                context.Response.Close();
                return;
            }

            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            var requestJson = await reader.ReadToEndAsync(cancellationToken);
            var responseJson = await HandleRequestAsync(requestJson, cancellationToken);

            if (responseJson is null)
            {
                context.Response.StatusCode = (int)HttpStatusCode.NoContent;
                context.Response.Close();
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(responseJson);
            context.Response.StatusCode = (int)HttpStatusCode.OK;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, cancellationToken);
            context.Response.Close();
        }
        catch (OperationCanceledException)
        {
            if (context.Response.OutputStream.CanWrite)
            {
                context.Response.Close();
            }
        }
        catch (HttpListenerException)
        {
            context.Response.Close();
        }
    }

    internal async Task<string?> HandleRequestAsync(string requestJson, CancellationToken cancellationToken)
    {
        JsonDocument? document = null;

        try
        {
            document = JsonDocument.Parse(requestJson);
            var root = document.RootElement;

            if (!root.TryGetProperty("method", out var methodProperty) || methodProperty.ValueKind != JsonValueKind.String)
            {
                return SerializeErrorResponse(root.TryGetProperty("id", out var missingMethodId) ? missingMethodId : (JsonElement?)null, -32600, "Missing JSON-RPC method.");
            }

            var hasId = root.TryGetProperty("id", out var idProperty);
            var method = methodProperty.GetString()!;

            if (!hasId && method.StartsWith("notifications/", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            object result = method switch
            {
                "initialize" => BuildInitializeResult(),
                "tools/list" => BuildToolsListResult(),
                "tools/call" => await HandleToolsCallAsync(root, cancellationToken),
                "ping" => new { },
                _ => throw new JsonRpcException(-32601, $"Method '{method}' is not supported.")
            };

            if (!hasId)
            {
                return null;
            }

            return JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = idProperty.Clone(),
                result
            }, JsonOptions);
        }
        catch (JsonRpcException ex)
        {
            return SerializeErrorResponse(document?.RootElement.TryGetProperty("id", out var id) == true ? id : (JsonElement?)null, ex.Code, ex.Message);
        }
        catch (JsonException ex)
        {
            return SerializeErrorResponse(document?.RootElement.TryGetProperty("id", out var id) == true ? id : (JsonElement?)null, -32700, ex.Message);
        }
        catch (Exception ex)
        {
            return SerializeErrorResponse(document?.RootElement.TryGetProperty("id", out var id) == true ? id : (JsonElement?)null, -32603, ex.Message);
        }
        finally
        {
            document?.Dispose();
        }
    }

    private object BuildInitializeResult()
    {
        return new
        {
            protocolVersion = ProtocolVersion,
            capabilities = new
            {
                tools = new
                {
                    listChanged = false
                }
            },
            serverInfo = new
            {
                name = "csharp-modernization-analyzer",
                version = "0.1.0"
            }
        };
    }

    private object BuildToolsListResult()
    {
        return new
        {
            tools = _tools.Values.Select(static tool => new
            {
                name = tool.Name,
                description = tool.Description,
                inputSchema = tool.InputSchema
            })
        };
    }

    private async Task<object> HandleToolsCallAsync(JsonElement request, CancellationToken cancellationToken)
    {
        if (!request.TryGetProperty("params", out var paramsElement) || paramsElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonRpcException(-32602, "tools/call requires params.");
        }

        if (!paramsElement.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String)
        {
            throw new JsonRpcException(-32602, "tools/call requires a tool name.");
        }

        var toolName = nameElement.GetString()!;
        if (!_tools.TryGetValue(toolName, out var tool))
        {
            return CreateToolErrorResult($"Unknown tool '{toolName}'.");
        }

        var arguments = paramsElement.TryGetProperty("arguments", out var argumentsElement)
            ? argumentsElement
            : default;

        try
        {
            var result = await tool.ExecuteAsync(arguments, cancellationToken);
            return new
            {
                content = new[]
                {
                    new
                    {
                        type = "text",
                        text = JsonSerializer.Serialize(result, JsonOptions)
                    }
                },
                structuredContent = result
            };
        }
        catch (Exception ex)
        {
            return CreateToolErrorResult(ex.Message);
        }
    }

    private static object CreateToolErrorResult(string message)
    {
        return new
        {
            content = new[]
            {
                new
                {
                    type = "text",
                    text = message
                }
            },
            isError = true
        };
    }

    private static string SerializeErrorResponse(JsonElement? idElement, int code, string message)
    {
        object? id = idElement is null || idElement.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? null
            : idElement.Value.Clone();
        return JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            error = new
            {
                code,
                message
            }
        }, JsonOptions);
    }

    private static async Task<string?> ReadMessageAsync(Stream stream, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        while (true)
        {
            var line = await ReadLineAsync(stream, cancellationToken);
            if (line is null)
            {
                return headers.Count == 0 ? null : throw new EndOfStreamException("Unexpected end of stream while reading headers.");
            }

            if (line.Length == 0)
            {
                break;
            }

            var separatorIndex = line.IndexOf(':');
            if (separatorIndex <= 0)
            {
                throw new InvalidDataException($"Invalid header line '{line}'.");
            }

            headers[line[..separatorIndex].Trim()] = line[(separatorIndex + 1)..].Trim();
        }

        if (!headers.TryGetValue("Content-Length", out var contentLengthValue) || !int.TryParse(contentLengthValue, out var contentLength) || contentLength < 0)
        {
            throw new InvalidDataException("Missing or invalid Content-Length header.");
        }

        var buffer = new byte[contentLength];
        await ReadExactlyAsync(stream, buffer, cancellationToken);
        return Encoding.UTF8.GetString(buffer);
    }

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        var buffer = new byte[1];

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken);
            if (read == 0)
            {
                if (bytes.Count == 0)
                {
                    return null;
                }

                break;
            }

            if (buffer[0] == (byte)'\n')
            {
                break;
            }

            if (buffer[0] != (byte)'\r')
            {
                bytes.Add(buffer[0]);
            }
        }

        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("Unexpected end of stream while reading message payload.");
            }

            offset += read;
        }
    }

    private static async Task WriteMessageAsync(Stream stream, string json, CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}

internal interface IMcpTool
{
    string Name { get; }
    string Description { get; }
    JsonObject InputSchema { get; }
    Task<object> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken);
}

internal sealed class AnalyzerService
{
    private static readonly IReadOnlyList<IPatternDetector> Detectors =
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
    ];

    private static readonly Lazy<IReadOnlyList<MetadataReference>> MetadataReferences = new(CreateMetadataReferences);
    private readonly PatchGenerator _patchGenerator = new();

    public async Task<IReadOnlyList<SuggestionResponse>> ScanFilesAsync(IReadOnlyList<string> inputPaths, string? configPath, CancellationToken cancellationToken)
    {
        var repoRoot = FindRepoRoot(Directory.GetCurrentDirectory());
        var config = LoadConfig(repoRoot, configPath);
        var files = ExpandInputPaths(inputPaths, repoRoot, config);
        var analysis = await AnalyzeAsync(repoRoot, config, files, cancellationToken);

        return analysis.Results
            .OrderBy(static result => result.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static result => result.LineSpan.Start.Line)
            .Select(result => ToSuggestionResponse(result, repoRoot, config))
            .ToList();
    }

    public async Task<IReadOnlyList<SuggestionResponse>> ScanDiffAsync(bool stagedOnly, string? configPath, CancellationToken cancellationToken)
    {
        var repoRoot = FindRepoRoot(Directory.GetCurrentDirectory());
        var config = LoadConfig(repoRoot, configPath);
        var changedFiles = await GetChangedFilesAsync(repoRoot, stagedOnly, cancellationToken);
        var analysis = await AnalyzeAsync(repoRoot, config, changedFiles, cancellationToken);

        return analysis.Results
            .OrderBy(static result => result.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static result => result.LineSpan.Start.Line)
            .Select(result => ToSuggestionResponse(result, repoRoot, config))
            .ToList();
    }

    public async Task<SuggestionResponse?> GetSuggestionAsync(string filePath, string ruleId, int line, string? configPath, CancellationToken cancellationToken)
    {
        var repoRoot = FindRepoRoot(ResolvePath(filePath, Directory.GetCurrentDirectory()));
        var config = LoadConfig(repoRoot, configPath);
        var absolutePath = ResolvePath(filePath, repoRoot);
        var analysis = await AnalyzeAsync(repoRoot, config, [absolutePath], cancellationToken);
        var match = FindMatchingSuggestion(analysis.Results, absolutePath, ruleId, line);
        return match is null ? null : ToSuggestionResponse(match, repoRoot, config);
    }

    public async Task<ApplySuggestionResponse?> ApplySuggestionAsync(string filePath, string ruleId, int line, string? configPath, CancellationToken cancellationToken)
    {
        var repoRoot = FindRepoRoot(ResolvePath(filePath, Directory.GetCurrentDirectory()));
        var config = LoadConfig(repoRoot, configPath);
        var absolutePath = ResolvePath(filePath, repoRoot);
        var analysis = await AnalyzeAsync(repoRoot, config, [absolutePath], cancellationToken);
        var match = FindMatchingSuggestion(analysis.Results, absolutePath, ruleId, line);
        if (match is null)
        {
            return null;
        }

        var source = await File.ReadAllTextAsync(absolutePath, cancellationToken);
        var modified = ApplySuggestion(source, match);
        var suggestion = ToSuggestionResponse(match, repoRoot, config);

        return new ApplySuggestionResponse
        {
            FilePath = suggestion.FilePath,
            RuleId = suggestion.RuleId,
            Line = suggestion.Line,
            ModifiedSource = modified,
            BeforeCode = suggestion.BeforeCode,
            AfterCode = suggestion.AfterCode,
            UnifiedDiff = suggestion.UnifiedDiff
        };
    }

    public IReadOnlyList<RuleResponse> ListRules(string? configPath)
    {
        var repoRoot = FindRepoRoot(Directory.GetCurrentDirectory());
        var config = LoadConfig(repoRoot, configPath);

        return Detectors
            .OrderBy(static detector => detector.RuleId, StringComparer.OrdinalIgnoreCase)
            .Select(detector => new RuleResponse
            {
                RuleId = detector.RuleId,
                RuleName = detector.RuleName,
                Description = DescribeRule(detector.RuleId),
                Enabled = config.IsRuleEnabled(detector.RuleId),
                Severity = ToSeverityText(config.GetRuleSeverity(detector.RuleId) ?? Severity.Suggestion),
                MinLanguageVersion = $"{detector.MinimumLangVersion.Major}.{detector.MinimumLangVersion.Minor}"
            })
            .ToList();
    }

    public StandardsResponse GetStandards(string repoPath, string? configPath)
    {
        var resolvedRepoPath = ResolvePath(repoPath, Directory.GetCurrentDirectory());
        var repoRoot = FindRepoRoot(resolvedRepoPath);
        var config = LoadConfig(repoRoot, configPath);
        var editorConfigPath = Path.Combine(repoRoot, ".editorconfig");
        var styleCopPath = Path.Combine(repoRoot, "stylecop.json");
        var ruleSetFiles = SafeGetFiles(repoRoot, "*.ruleset");
        var globalConfigFiles = SafeGetFiles(repoRoot, ".globalconfig");

        var response = new StandardsResponse
        {
            RepoPath = NormalizePath(Path.GetRelativePath(Directory.GetCurrentDirectory(), repoRoot)),
            EditorConfig = new StandardsSourceResponse
            {
                Found = File.Exists(editorConfigPath),
                Path = File.Exists(editorConfigPath) ? ".editorconfig" : null,
                Details = File.Exists(editorConfigPath)
                    ? $"{CountEditorConfigPreferences(editorConfigPath)} C# preferences found"
                    : null
            },
            StyleCop = new StandardsSourceResponse
            {
                Found = File.Exists(styleCopPath),
                Path = File.Exists(styleCopPath) ? "stylecop.json" : null,
                Details = File.Exists(styleCopPath) ? "StyleCop settings file found" : null
            },
            RuleSetFiles = ruleSetFiles.Select(path => NormalizePath(Path.GetRelativePath(repoRoot, path))).ToList(),
            GlobalConfigFiles = globalConfigFiles.Select(path => NormalizePath(Path.GetRelativePath(repoRoot, path))).ToList(),
            ExternalStandards = config.ExternalStandardUrls.Select(static url => new ExternalStandardResponse
            {
                Url = url
            }).ToList()
        };

        if (response.EditorConfig.Found)
        {
            response.ActiveSources.Add("editorconfig");
        }

        if (response.StyleCop.Found)
        {
            response.ActiveSources.Add("stylecop");
        }

        if (response.RuleSetFiles.Count > 0 || response.GlobalConfigFiles.Count > 0)
        {
            response.ActiveSources.Add("analyzer-config");
        }

        if (response.ExternalStandards.Count > 0)
        {
            response.ActiveSources.Add("external-standards");
        }

        return response;
    }

    private static async Task<AnalysisResult> AnalyzeAsync(string repoRoot, ModernizationConfig config, IReadOnlyList<string> files, CancellationToken cancellationToken)
    {
        var filteredFiles = files
            .Where(File.Exists)
            .Where(static path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Where(path => !config.IsExcluded(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (filteredFiles.Count == 0)
        {
            return new AnalysisResult { Results = [] };
        }

        var standardsResolver = new Analyzer.Core.Standards.StandardsResolver();
        var standards = await standardsResolver.ResolveAsync(repoRoot, config, cancellationToken);
        var compilation = await BuildCompilationAsync(filteredFiles, cancellationToken);
        var engine = new DetectionEngine(Detectors, standards, config);
        var results = await engine.AnalyzeFilesAsync(filteredFiles, compilation, cancellationToken);

        return new AnalysisResult
        {
            Results = results
        };
    }

    private static async Task<CSharpCompilation> BuildCompilationAsync(IReadOnlyList<string> files, CancellationToken cancellationToken)
    {
        var syntaxTrees = new List<SyntaxTree>(files.Count);
        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);

        foreach (var file in files)
        {
            var text = await File.ReadAllTextAsync(file, cancellationToken);
            syntaxTrees.Add(CSharpSyntaxTree.ParseText(text, parseOptions, file));
        }

        return CSharpCompilation.Create(
            assemblyName: "Analyzer.Mcp.Workspace",
            syntaxTrees: syntaxTrees,
            references: MetadataReferences.Value,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static IReadOnlyList<string> ExpandInputPaths(IReadOnlyList<string> inputPaths, string repoRoot, ModernizationConfig config)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var inputPath in inputPaths)
        {
            var resolvedPath = ResolvePath(inputPath, repoRoot);
            if (File.Exists(resolvedPath) && resolvedPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                files.Add(resolvedPath);
                continue;
            }

            if (!Directory.Exists(resolvedPath))
            {
                continue;
            }

            foreach (var file in Directory.GetFiles(resolvedPath, "*.cs", SearchOption.AllDirectories))
            {
                if (!config.IsExcluded(file))
                {
                    files.Add(file);
                }
            }
        }

        return files.ToList();
    }

    private static DetectionResult? FindMatchingSuggestion(IEnumerable<DetectionResult> results, string absolutePath, string ruleId, int line)
    {
        return results.FirstOrDefault(result =>
            result.FilePath.Equals(absolutePath, StringComparison.OrdinalIgnoreCase) &&
            result.RuleId.Equals(ruleId, StringComparison.OrdinalIgnoreCase) &&
            line >= result.LineSpan.Start.Line + 1 &&
            line <= result.LineSpan.End.Line + 1);
    }

    private SuggestionResponse ToSuggestionResponse(DetectionResult result, string repoRoot, ModernizationConfig config)
    {
        return new SuggestionResponse
        {
            RuleId = result.RuleId,
            RuleName = result.RuleName,
            Description = result.Description,
            FilePath = NormalizePath(Path.GetRelativePath(repoRoot, result.FilePath)),
            Line = result.LineSpan.Start.Line + 1,
            EndLine = result.LineSpan.End.Line + 1,
            Severity = ToSeverityText(config.GetRuleSeverity(result.RuleId) ?? result.Severity),
            Explanation = string.IsNullOrWhiteSpace(result.Explanation)
                ? $"Rule {result.RuleId} ({result.RuleName}): {result.Description}"
                : result.Explanation!,
            BeforeCode = result.OriginalCode,
            AfterCode = result.SuggestedCode,
            UnifiedDiff = _patchGenerator.GenerateUnifiedDiff(result)
        };
    }

    private static string ApplySuggestion(string source, DetectionResult result)
    {
        return string.Concat(source.AsSpan(0, result.Span.Start), result.SuggestedCode, source.AsSpan(result.Span.End));
    }

    private static ModernizationConfig LoadConfig(string repoRoot, string? configPath)
    {
        var resolvedConfigPath = ResolvePath(string.IsNullOrWhiteSpace(configPath) ? ".modernization.yml" : configPath, repoRoot);
        return ModernizationConfig.LoadFromFile(resolvedConfigPath);
    }

    private static string ResolvePath(string path, string basePath)
    {
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(basePath, path));
    }

    private static string FindRepoRoot(string startPath)
    {
        var current = File.Exists(startPath) ? Path.GetDirectoryName(startPath) : startPath;
        current = Path.GetFullPath(current ?? Directory.GetCurrentDirectory());

        while (!string.IsNullOrEmpty(current))
        {
            if (Directory.Exists(Path.Combine(current, ".git")))
            {
                return current;
            }

            current = Path.GetDirectoryName(current);
        }

        return Path.GetFullPath(startPath);
    }

    private static IReadOnlyList<MetadataReference> CreateMetadataReferences()
    {
        var trustedAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (!string.IsNullOrWhiteSpace(trustedAssemblies))
        {
            return trustedAssemblies
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(static path => (MetadataReference)MetadataReference.CreateFromFile(path))
                .ToList();
        }

        return
        [
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Console).Assembly.Location)
        ];
    }

    private static async Task<IReadOnlyList<string>> GetChangedFilesAsync(string repoRoot, bool stagedOnly, CancellationToken cancellationToken)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var relativePath in await RunGitLinesAsync(repoRoot, ["diff", "--name-only", "--cached", "--diff-filter=ACMR", "--", "*.cs"], cancellationToken))
        {
            files.Add(ResolvePath(relativePath, repoRoot));
        }

        if (!stagedOnly)
        {
            foreach (var relativePath in await RunGitLinesAsync(repoRoot, ["diff", "--name-only", "--diff-filter=ACMR", "--", "*.cs"], cancellationToken))
            {
                files.Add(ResolvePath(relativePath, repoRoot));
            }

            foreach (var relativePath in await RunGitLinesAsync(repoRoot, ["ls-files", "--others", "--exclude-standard", "--", "*.cs"], cancellationToken))
            {
                files.Add(ResolvePath(relativePath, repoRoot));
            }
        }

        return files.ToList();
    }

    private static async Task<IReadOnlyList<string>> RunGitLinesAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start git process.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? "Git diff failed." : stderr.Trim());
        }

        return stdout
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Trim())
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .ToList();
    }

    private static IReadOnlyList<string> SafeGetFiles(string rootPath, string searchPattern)
    {
        return Directory.Exists(rootPath)
            ? Directory.GetFiles(rootPath, searchPattern, SearchOption.AllDirectories)
                .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .ToList()
            : [];
    }

    private static int CountEditorConfigPreferences(string editorConfigPath)
    {
        var lines = File.ReadAllLines(editorConfigPath);
        var inCSharpSection = false;
        var count = 0;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('['))
            {
                inCSharpSection = trimmed.Contains("*.cs", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("*.{cs", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (inCSharpSection && trimmed.Contains('='))
            {
                count++;
            }
        }

        return count;
    }

    private static string DescribeRule(string ruleId)
    {
        return ruleId switch
        {
            "MOD001" => "Use 'var' where explicit local types are redundant.",
            "MOD002" => "Use null-conditional or null-coalescing operators instead of explicit null checks.",
            "MOD003" => "Use string interpolation instead of string.Format.",
            "MOD004" => "Use pattern matching instead of explicit casts and null checks.",
            "MOD005" => "Convert repeated if/else chains into switch expressions.",
            "MOD006" => "Use using declarations when scope already extends to the end of a block.",
            "MOD007" => "Use ??= for initialize-if-null patterns.",
            "MOD008" => "Convert block-scoped namespaces to file-scoped namespaces.",
            "MOD009" => "Use target-typed new when the type is already known.",
            "MOD010" => "Use collection expressions for arrays and collection initializers.",
            "MOD011" => "Use raw string literals for multiline or heavily escaped text.",
            "MOD012" => "Use primary constructors for simple dependency-assignment constructors.",
            _ => "Modernization rule"
        };
    }

    private static string ToSeverityText(Severity severity)
    {
        return severity.ToString().ToLowerInvariant();
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/');
    }
}

internal sealed class JsonRpcException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

internal sealed class AnalysisResult
{
    public required IReadOnlyList<DetectionResult> Results { get; init; }
}

internal sealed class SuggestionResponse
{
    [JsonPropertyName("rule_id")]
    public required string RuleId { get; init; }

    [JsonPropertyName("rule_name")]
    public required string RuleName { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("file_path")]
    public required string FilePath { get; init; }

    [JsonPropertyName("line")]
    public required int Line { get; init; }

    [JsonPropertyName("end_line")]
    public required int EndLine { get; init; }

    [JsonPropertyName("severity")]
    public required string Severity { get; init; }

    [JsonPropertyName("explanation")]
    public required string Explanation { get; init; }

    [JsonPropertyName("before_code")]
    public required string BeforeCode { get; init; }

    [JsonPropertyName("after_code")]
    public required string AfterCode { get; init; }

    [JsonPropertyName("unified_diff")]
    public required string UnifiedDiff { get; init; }
}

internal sealed class ApplySuggestionResponse
{
    [JsonPropertyName("file_path")]
    public required string FilePath { get; init; }

    [JsonPropertyName("rule_id")]
    public required string RuleId { get; init; }

    [JsonPropertyName("line")]
    public required int Line { get; init; }

    [JsonPropertyName("modified_source")]
    public required string ModifiedSource { get; init; }

    [JsonPropertyName("before_code")]
    public required string BeforeCode { get; init; }

    [JsonPropertyName("after_code")]
    public required string AfterCode { get; init; }

    [JsonPropertyName("unified_diff")]
    public required string UnifiedDiff { get; init; }
}

internal sealed class RuleResponse
{
    [JsonPropertyName("rule_id")]
    public required string RuleId { get; init; }

    [JsonPropertyName("rule_name")]
    public required string RuleName { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("enabled")]
    public required bool Enabled { get; init; }

    [JsonPropertyName("severity")]
    public required string Severity { get; init; }

    [JsonPropertyName("min_language_version")]
    public required string MinLanguageVersion { get; init; }
}

internal sealed class StandardsResponse
{
    [JsonPropertyName("repo_path")]
    public required string RepoPath { get; init; }

    [JsonPropertyName("editorconfig")]
    public required StandardsSourceResponse EditorConfig { get; init; }

    [JsonPropertyName("stylecop")]
    public required StandardsSourceResponse StyleCop { get; init; }

    [JsonPropertyName("ruleset_files")]
    public required List<string> RuleSetFiles { get; init; }

    [JsonPropertyName("globalconfig_files")]
    public required List<string> GlobalConfigFiles { get; init; }

    [JsonPropertyName("external_standards")]
    public required List<ExternalStandardResponse> ExternalStandards { get; init; }

    [JsonPropertyName("active_sources")]
    public List<string> ActiveSources { get; } = [];
}

internal sealed class StandardsSourceResponse
{
    [JsonPropertyName("found")]
    public required bool Found { get; init; }

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("details")]
    public string? Details { get; init; }
}

internal sealed class ExternalStandardResponse
{
    [JsonPropertyName("url")]
    public required string Url { get; init; }
}
