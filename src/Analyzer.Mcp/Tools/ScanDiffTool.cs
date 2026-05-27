using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Analyzer.Mcp.Tools;

internal sealed class ScanDiffTool(AnalyzerService analyzer) : IMcpTool
{
    public string Name => "scan_diff";
    public string Description => "Analyze staged or uncommitted C# changes for modernization opportunities.";
    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["staged_only"] = new JsonObject
            {
                ["type"] = "boolean",
                ["default"] = false
            },
            ["config_path"] = new JsonObject
            {
                ["type"] = "string",
                ["default"] = ".modernization.yml"
            }
        },
        ["additionalProperties"] = false
    };

    public async Task<object> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var request = arguments.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? new ScanDiffArguments()
            : JsonSerializer.Deserialize<ScanDiffArguments>(arguments.GetRawText()) ?? new ScanDiffArguments();

        return await analyzer.ScanDiffAsync(request.StagedOnly, request.ConfigPath, cancellationToken);
    }

    private sealed class ScanDiffArguments
    {
        [JsonPropertyName("staged_only")]
        public bool StagedOnly { get; init; }

        [JsonPropertyName("config_path")]
        public string ConfigPath { get; init; } = ".modernization.yml";
    }
}
