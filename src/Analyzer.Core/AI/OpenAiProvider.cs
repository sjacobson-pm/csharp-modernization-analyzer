using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Analyzer.Core.Detection;

namespace Analyzer.Core.AI;

/// <summary>
/// OpenAI-based AI provider for generating explanations and refining suggestions.
/// </summary>
public sealed class OpenAiProvider : IAiProvider
{
    private static readonly HttpClient SharedHttpClient = new();
    private readonly HttpClient httpClient;
    private readonly string model;

    public OpenAiProvider(string apiKey, string model = "gpt-4o", HttpClient? httpClient = null)
    {
        this.model = model;
        this.httpClient = httpClient ?? SharedHttpClient;

        if (!this.httpClient.DefaultRequestHeaders.Contains("Authorization"))
        {
            this.httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
        }
    }

    public async Task<string> GenerateExplanationAsync(DetectionResult result, CancellationToken cancellationToken = default)
    {
        var prompt = $"""
                      Explain in 1-2 sentences why this C# modernization is beneficial:

                      Rule: {result.RuleId} - {result.RuleName}
                      Original code:
                      ```csharp
                      {result.OriginalCode}
                      ```

                      Suggested modern equivalent:
                      ```csharp
                      {result.SuggestedCode}
                      ```

                      Focus on readability, safety, or performance benefits. Be concise.
                      """;

        return await this.CallApiAsync(prompt, cancellationToken);
    }

    public async Task<string> RefineSuggestionAsync(DetectionResult result, string surroundingContext, CancellationToken cancellationToken = default)
    {
        var prompt = $"""
                      Refine this C# code modernization suggestion to fit naturally in its context.
                      Return ONLY the refined code, no explanation.

                      Context:
                      ```csharp
                      {surroundingContext}
                      ```

                      Original code to replace:
                      ```csharp
                      {result.OriginalCode}
                      ```

                      Initial suggestion:
                      ```csharp
                      {result.SuggestedCode}
                      ```
                      """;

        return await this.CallApiAsync(prompt, cancellationToken);
    }

    private async Task<string> CallApiAsync(string prompt, CancellationToken cancellationToken)
    {
        var request = new
        {
            this.model,
            messages = new[]
            {
                new { role = "system", content = "You are a C# modernization expert. Be concise and precise." },
                new { role = "user", content = prompt },
            },
            max_tokens = 500,
            temperature = 0.3,
        };

        var response = await this.httpClient.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", request, cancellationToken);

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);

        return json.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
    }
}
