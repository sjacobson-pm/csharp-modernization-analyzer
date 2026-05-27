using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Analyzer.Mcp.Tools;

internal sealed class GetStandardsTool(AnalyzerService analyzer) : IMcpTool
{
    public string Name => "get_standards";
    public string Description => "Report which repository standards sources are active.";
    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["repo_path"] = new JsonObject
            {
                ["type"] = "string",
                ["default"] = "."
            },
            ["config_path"] = new JsonObject
            {
                ["type"] = "string",
                ["default"] = ".modernization.yml"
            }
        },
        ["additionalProperties"] = false
    };

    public Task<object> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var request = arguments.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? new GetStandardsArguments()
            : JsonSerializer.Deserialize<GetStandardsArguments>(arguments.GetRawText()) ?? new GetStandardsArguments();

        return Task.FromResult<object>(analyzer.GetStandards(request.RepoPath, request.ConfigPath));
    }

    private sealed class GetStandardsArguments
    {
        [JsonPropertyName("repo_path")]
        public string RepoPath { get; init; } = ".";

        [JsonPropertyName("config_path")]
        public string ConfigPath { get; init; } = ".modernization.yml";
    }
}
