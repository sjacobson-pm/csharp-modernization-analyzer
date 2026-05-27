using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Analyzer.Mcp.Tools;

internal sealed class GetSuggestionTool(AnalyzerService analyzer) : IMcpTool
{
    public string Name => "get_suggestion";
    public string Description => "Return detailed explanation and diff for a specific finding.";
    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["rule_id"] = new JsonObject { ["type"] = "string" },
            ["file_path"] = new JsonObject { ["type"] = "string" },
            ["line"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 },
            ["config_path"] = new JsonObject { ["type"] = "string", ["default"] = ".modernization.yml" }
        },
        ["required"] = new JsonArray("rule_id", "file_path", "line"),
        ["additionalProperties"] = false
    };

    public async Task<object> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        if (arguments.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            throw new InvalidOperationException("get_suggestion requires rule_id, file_path, and line.");
        }

        var request = JsonSerializer.Deserialize<GetSuggestionArguments>(arguments.GetRawText())
            ?? throw new InvalidOperationException("Invalid get_suggestion arguments.");

        var suggestion = await analyzer.GetSuggestionAsync(request.FilePath, request.RuleId, request.Line, request.ConfigPath, cancellationToken);
        return suggestion is null ? (object)new { message = "Suggestion not found." } : suggestion;
    }

    private sealed class GetSuggestionArguments
    {
        [JsonPropertyName("rule_id")]
        public string RuleId { get; init; } = string.Empty;

        [JsonPropertyName("file_path")]
        public string FilePath { get; init; } = string.Empty;

        [JsonPropertyName("line")]
        public int Line { get; init; }

        [JsonPropertyName("config_path")]
        public string ConfigPath { get; init; } = ".modernization.yml";
    }
}
