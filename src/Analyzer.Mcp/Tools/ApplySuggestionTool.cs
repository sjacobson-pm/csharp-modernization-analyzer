using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Analyzer.Mcp.Tools;

internal sealed class ApplySuggestionTool(AnalyzerService analyzer) : IMcpTool
{
    public string Name => "apply_suggestion";
    public string Description => "Apply a suggestion in-memory and return the modified source for review.";
    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["file_path"] = new JsonObject { ["type"] = "string" },
            ["rule_id"] = new JsonObject { ["type"] = "string" },
            ["line"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 },
            ["config_path"] = new JsonObject { ["type"] = "string", ["default"] = ".modernization.yml" }
        },
        ["required"] = new JsonArray("file_path", "rule_id", "line"),
        ["additionalProperties"] = false
    };

    public async Task<object> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        if (arguments.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            throw new InvalidOperationException("apply_suggestion requires file_path, rule_id, and line.");
        }

        var request = JsonSerializer.Deserialize<ApplySuggestionArguments>(arguments.GetRawText())
            ?? throw new InvalidOperationException("Invalid apply_suggestion arguments.");

        var suggestion = await analyzer.ApplySuggestionAsync(request.FilePath, request.RuleId, request.Line, request.ConfigPath, cancellationToken);
        return suggestion is null ? (object)new { message = "Suggestion not found." } : suggestion;
    }

    private sealed class ApplySuggestionArguments
    {
        [JsonPropertyName("file_path")]
        public string FilePath { get; init; } = string.Empty;

        [JsonPropertyName("rule_id")]
        public string RuleId { get; init; } = string.Empty;

        [JsonPropertyName("line")]
        public int Line { get; init; }

        [JsonPropertyName("config_path")]
        public string ConfigPath { get; init; } = ".modernization.yml";
    }
}
