using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Analyzer.Mcp.Tools;

internal sealed class ListRulesTool(AnalyzerService analyzer) : IMcpTool
{
    public string Name => "list_rules";
    public string Description => "List implemented modernization rules with configuration and severity.";
    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
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
            ? new ListRulesArguments()
            : JsonSerializer.Deserialize<ListRulesArguments>(arguments.GetRawText()) ?? new ListRulesArguments();

        return Task.FromResult<object>(analyzer.ListRules(request.ConfigPath));
    }

    private sealed class ListRulesArguments
    {
        [JsonPropertyName("config_path")]
        public string ConfigPath { get; init; } = ".modernization.yml";
    }
}
