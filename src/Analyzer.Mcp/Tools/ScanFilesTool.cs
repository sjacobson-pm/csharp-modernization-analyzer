using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Analyzer.Mcp.Tools;

internal sealed class ScanFilesTool(AnalyzerService analyzer) : IMcpTool
{
    public string Name => "scan_files";
    public string Description => "Analyze specific files or directories for modernization opportunities.";
    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["paths"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
                ["minItems"] = 1
            },
            ["config_path"] = new JsonObject
            {
                ["type"] = "string",
                ["default"] = ".modernization.yml"
            }
        },
        ["required"] = new JsonArray("paths"),
        ["additionalProperties"] = false
    };

    public async Task<object> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var request = arguments.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? new ScanFilesArguments()
            : JsonSerializer.Deserialize<ScanFilesArguments>(arguments.GetRawText()) ?? new ScanFilesArguments();

        if (request.Paths.Count == 0)
        {
            throw new InvalidOperationException("scan_files requires at least one path.");
        }

        return await analyzer.ScanFilesAsync(request.Paths, request.ConfigPath, cancellationToken);
    }

    private sealed class ScanFilesArguments
    {
        [JsonPropertyName("paths")]
        public List<string> Paths { get; init; } = [];

        [JsonPropertyName("config_path")]
        public string ConfigPath { get; init; } = ".modernization.yml";
    }
}
