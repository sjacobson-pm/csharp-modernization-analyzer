using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Analyzer.Extension.Handlers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Analyzer.Extension;

public sealed class CopilotMessageHandler(IHttpClientFactory httpClientFactory, ILogger<CopilotMessageHandler> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IntentParser intentParser = new();
    private readonly ScanHandler scanHandler = new(httpClientFactory);
    private readonly ExplainHandler explainHandler = new();
    private readonly ListRulesHandler listRulesHandler = new();

    public async Task HandleAsync(HttpContext context)
    {
        var cancellationToken = context.RequestAborted;
        await ConfigureSseResponseAsync(context.Response, cancellationToken);

        CopilotRequestContext requestContext;

        try { requestContext = await CopilotRequestContext.FromHttpRequestAsync(context.Request, cancellationToken); }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Received invalid Copilot webhook payload.");
            await StreamResponseAsync(context.Response, BuildErrorResponse("I couldn't parse the Copilot webhook payload."), cancellationToken);

            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process Copilot webhook payload.");

            await StreamResponseAsync(
                context.Response,
                BuildErrorResponse("Something went wrong while processing the Copilot request."),
                cancellationToken);

            return;
        }

        if (string.IsNullOrWhiteSpace(requestContext.UserMessage))
        {
            await StreamResponseAsync(
                context.Response,
                BuildErrorResponse("I couldn't find the user's message in the webhook payload."),
                cancellationToken);

            return;
        }

        var intent = this.intentParser.Parse(requestContext.UserMessage);

        var response = intent switch
        {
            ScanPrIntent scanPrIntent => await this.scanHandler.HandleAsync(scanPrIntent, requestContext, cancellationToken),
            ScanDirectoryIntent scanDirectoryIntent => await this.scanHandler.HandleAsync(scanDirectoryIntent, requestContext, cancellationToken),
            ScanFileIntent scanFileIntent => await this.scanHandler.HandleAsync(scanFileIntent, requestContext, cancellationToken),
            ExplainRuleIntent explainRuleIntent => this.explainHandler.Handle(explainRuleIntent),
            ListRulesIntent listRulesIntent => this.listRulesHandler.Handle(listRulesIntent),
            _ => BuildHelpResponse(),
        };

        await StreamResponseAsync(context.Response, response, cancellationToken);
    }

    private static async Task ConfigureSseResponseAsync(HttpResponse response, CancellationToken cancellationToken)
    {
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream";
        response.Headers["Cache-Control"] = "no-cache";
        response.Headers["Connection"] = "keep-alive";
        response.Headers["X-Accel-Buffering"] = "no";
        await response.Body.FlushAsync(cancellationToken);
    }

    private static async Task StreamResponseAsync(HttpResponse response, CopilotResponse copilotResponse, CancellationToken cancellationToken)
    {
        await WriteEventAsync(
            response,
            new { type = "copilot_confirmation", title = copilotResponse.ConfirmationTitle, message = copilotResponse.ConfirmationMessage },
            cancellationToken);

        if (copilotResponse.References.Count > 0)
        {
            await WriteEventAsync(
                response,
                new
                {
                    type = "copilot_references",
                    references = copilotResponse.References.Select(reference => new { type = reference.Type, path = reference.Path }),
                },
                cancellationToken);
        }

        await WriteMarkdownAsync(response, copilotResponse.Content, cancellationToken);
        await response.WriteAsync("data: [DONE]\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    private static async Task WriteMarkdownAsync(HttpResponse response, string markdown, CancellationToken cancellationToken)
    {
        foreach (var chunk in Chunk(markdown, 1800))
        {
            await WriteEventAsync(response, new { choices = new[] { new { index = 0, delta = new { content = chunk } } } }, cancellationToken);
        }
    }

    private static async Task WriteEventAsync(HttpResponse response, object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        await response.WriteAsync($"data: {json}\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    private static IEnumerable<string> Chunk(string content, int maxChunkLength)
    {
        if (string.IsNullOrEmpty(content))
        {
            yield return string.Empty;

            yield break;
        }

        var builder = new StringBuilder();

        foreach (var line in content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var candidateLength = builder.Length == 0 ? line.Length : builder.Length + line.Length + 1;

            if (candidateLength > maxChunkLength && builder.Length > 0)
            {
                yield return builder.ToString();

                builder.Clear();
            }

            if (builder.Length > 0) { builder.Append('\n'); }

            builder.Append(line);
        }

        if (builder.Length > 0) { yield return builder.ToString(); }
    }

    private static CopilotResponse BuildHelpResponse()
    {
        return new CopilotResponse(
            "Ready to modernize",
            "I can scan code, explain rules, or list available rules.",
            """
            Try one of these commands:
            - `scan this PR`
            - `scan src/Services/`
            - `what can be modernized in this file`
            - `explain MOD004`
            - `what rules are available`
            """,
            []);
    }

    private static CopilotResponse BuildErrorResponse(string message)
    {
        return new CopilotResponse(
            "Request failed",
            "The Copilot Extension could not process the request.",
            $"{message}\n\nTry `what rules are available` or `scan src/`.",
            []);
    }
}

internal sealed record CopilotResponse(
    string ConfirmationTitle,
    string ConfirmationMessage,
    string Content,
    IReadOnlyList<CopilotReference> References);

internal sealed record CopilotReference(string Type, string Path);

internal sealed class CopilotRequestContext
{
    public required string UserMessage { get; init; }

    public string? RepositoryOwner { get; init; }

    public string? RepositoryName { get; init; }

    public int? PullRequestNumber { get; init; }

    public IReadOnlyList<string> ReferencedPaths { get; init; } = [];

    public string? RepositoryFullName =>
        !string.IsNullOrWhiteSpace(this.RepositoryOwner) && !string.IsNullOrWhiteSpace(this.RepositoryName)
            ? $"{this.RepositoryOwner}/{this.RepositoryName}"
            : null;

    public static async Task<CopilotRequestContext> FromHttpRequestAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
        var root = document.RootElement;

        return new CopilotRequestContext
        {
            UserMessage = StripMention(ExtractLastMessage(root)),
            RepositoryOwner = ExtractRepositoryOwner(root),
            RepositoryName = ExtractRepositoryName(root),
            PullRequestNumber = ExtractPullRequestNumber(root),
            ReferencedPaths = ExtractReferencedPaths(root),
        };
    }

    private static string StripMention(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) { return string.Empty; }

        return Regex.Replace(message, "@modernize\\b[:;,]?\\s*", string.Empty, RegexOptions.IgnoreCase).Trim();
    }

    private static string ExtractLastMessage(JsonElement root)
    {
        if (!root.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array) { return string.Empty; }

        var lastMessage = string.Empty;

        foreach (var message in messages.EnumerateArray())
        {
            var text = ExtractText(message);

            if (!string.IsNullOrWhiteSpace(text)) { lastMessage = text; }
        }

        return lastMessage;
    }

    private static string ExtractText(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Object => ExtractObjectText(element),
            JsonValueKind.Array => string.Join(
                "\n",
                element.EnumerateArray().Select(ExtractText).Where(static text => !string.IsNullOrWhiteSpace(text))),
            _ => string.Empty,
        };
    }

    private static string ExtractObjectText(JsonElement element)
    {
        if (element.TryGetProperty("content", out var content))
        {
            var text = ExtractText(content);

            if (!string.IsNullOrWhiteSpace(text)) { return text; }
        }

        if (element.TryGetProperty("text", out var textProperty) && textProperty.ValueKind == JsonValueKind.String)
        {
            return textProperty.GetString() ?? string.Empty;
        }

        if (element.TryGetProperty("body", out var bodyProperty) && bodyProperty.ValueKind == JsonValueKind.String)
        {
            return bodyProperty.GetString() ?? string.Empty;
        }

        if (element.TryGetProperty("parts", out var parts))
        {
            var text = ExtractText(parts);

            if (!string.IsNullOrWhiteSpace(text)) { return text; }
        }

        return string.Empty;
    }

    private static string? ExtractRepositoryOwner(JsonElement root)
    {
        if (!root.TryGetProperty("repository", out var repository) || repository.ValueKind != JsonValueKind.Object) { return null; }

        if (repository.TryGetProperty("owner", out var owner) &&
            owner.ValueKind == JsonValueKind.Object &&
            owner.TryGetProperty("login", out var login)) { return login.GetString(); }

        if (repository.TryGetProperty("full_name", out var fullName) && fullName.ValueKind == JsonValueKind.String)
        {
            var parts = (fullName.GetString() ?? string.Empty).Split('/', 2, StringSplitOptions.RemoveEmptyEntries);

            return parts.Length == 2 ? parts[0] : null;
        }

        return null;
    }

    private static string? ExtractRepositoryName(JsonElement root)
    {
        if (!root.TryGetProperty("repository", out var repository) || repository.ValueKind != JsonValueKind.Object) { return null; }

        if (repository.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String) { return name.GetString(); }

        if (repository.TryGetProperty("full_name", out var fullName) && fullName.ValueKind == JsonValueKind.String)
        {
            var parts = (fullName.GetString() ?? string.Empty).Split('/', 2, StringSplitOptions.RemoveEmptyEntries);

            return parts.Length == 2 ? parts[1] : null;
        }

        return null;
    }

    private static int? ExtractPullRequestNumber(JsonElement root)
    {
        if (root.TryGetProperty("pull_request", out var pullRequest) &&
            pullRequest.ValueKind == JsonValueKind.Object &&
            pullRequest.TryGetProperty("number", out var numberProperty) &&
            numberProperty.TryGetInt32(out var number)) { return number; }

        return null;
    }

    private static IReadOnlyList<string> ExtractReferencedPaths(JsonElement root)
    {
        var paths = new List<string>();

        CollectPaths(root, "references", paths);
        CollectPaths(root, "files", paths);

        if (root.TryGetProperty("file", out var file))
        {
            var path = ExtractPath(file);

            if (!string.IsNullOrWhiteSpace(path)) { paths.Add(path); }
        }

        return paths.Where(static path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void CollectPaths(JsonElement root, string propertyName, List<string> paths)
    {
        if (!root.TryGetProperty(propertyName, out var collection) || collection.ValueKind != JsonValueKind.Array) { return; }

        foreach (var item in collection.EnumerateArray())
        {
            var path = ExtractPath(item);

            if (!string.IsNullOrWhiteSpace(path)) { paths.Add(path); }
        }
    }

    private static string? ExtractPath(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String) { return element.GetString(); }

        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty("path", out var pathProperty) &&
            pathProperty.ValueKind == JsonValueKind.String) { return pathProperty.GetString(); }

        return null;
    }
}
