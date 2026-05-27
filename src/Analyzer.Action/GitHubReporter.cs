using System.Text;
using Analyzer.Core.Detection;
using Octokit;

namespace Analyzer.Action;

internal sealed class GitHubReporter
{
    private const string SummaryMarker = "<!-- modernization-analyzer-summary -->";
    private readonly GitHubActionContext _context;
    private readonly GitHubClient? _client;
    private readonly int _maxSuggestions;

    public GitHubReporter(GitHubActionContext context, int maxSuggestions)
    {
        _context = context;
        _maxSuggestions = maxSuggestions;

        if (context.CanPostToPullRequest)
        {
            _client = new GitHubClient(new ProductHeaderValue("csharp-modernization-analyzer"))
            {
                Credentials = new Credentials(context.Token)
            };
        }
    }

    public async Task<int> ReportAsync(
        IReadOnlyList<DetectionResult> inlineResults,
        IReadOnlyList<DetectionResult> allResults,
        CancellationToken cancellationToken = default)
    {
        if (_client is null || !_context.PullRequestNumber.HasValue || string.IsNullOrWhiteSpace(_context.HeadSha))
        {
            Console.WriteLine("GitHub pull request context not available; skipping PR comments.");
            return 0;
        }

        var posted = await CreateReviewAsync(inlineResults.Take(_maxSuggestions).ToList(), cancellationToken);
        await UpsertSummaryCommentAsync(allResults, posted);
        return posted;
    }

    private async Task<int> CreateReviewAsync(IReadOnlyList<DetectionResult> inlineResults, CancellationToken cancellationToken)
    {
        if (_client is null || inlineResults.Count == 0)
        {
            return 0;
        }

        var payload = new
        {
            body = $"C# Modernization Analyzer posted {inlineResults.Count} inline suggestion(s).",
            @event = "COMMENT",
            commit_id = _context.HeadSha,
            comments = inlineResults.Select(BuildCommentPayload).ToArray()
        };

        try
        {
            var endpoint = new Uri($"https://api.github.com/repos/{_context.Owner}/{_context.Repo}/pulls/{_context.PullRequestNumber}/reviews");
            await _client.Connection.Post(endpoint, payload, "application/vnd.github+json", cancellationToken);
            return inlineResults.Count;
        }
        catch (ApiException ex)
        {
            Console.WriteLine($"Unable to post inline review comments: {ex.Message}");
            return 0;
        }
    }

    private static object BuildCommentPayload(DetectionResult result)
    {
        var startLine = result.LineSpan.Start.Line + 1;
        var endLine = Math.Max(result.LineSpan.End.Line + 1, startLine);
        var payload = new Dictionary<string, object?>
        {
            ["path"] = result.FilePath.Replace('\\', '/'),
            ["body"] = BuildSuggestionBody(result),
            ["side"] = "RIGHT",
            ["line"] = endLine
        };

        if (endLine > startLine)
        {
            payload["start_line"] = startLine;
            payload["start_side"] = "RIGHT";
        }

        return payload;
    }

    private static string BuildSuggestionBody(DetectionResult result)
    {
        var builder = new StringBuilder();
        builder.Append("**").Append(result.RuleId).Append("** - ").Append(result.RuleName).AppendLine("  ");
        builder.AppendLine(result.Description);
        builder.AppendLine();
        builder.AppendLine("```suggestion");
        builder.AppendLine(result.SuggestedCode.TrimEnd());
        builder.AppendLine("```");

        if (!string.IsNullOrWhiteSpace(result.Explanation))
        {
            builder.AppendLine();
            builder.Append("_AI explanation: ").Append(result.Explanation.Trim()).AppendLine("_");
        }

        return builder.ToString();
    }

    private async Task UpsertSummaryCommentAsync(IReadOnlyList<DetectionResult> allResults, int postedCount)
    {
        if (_client is null || !_context.PullRequestNumber.HasValue)
        {
            return;
        }

        var summaryBody = BuildSummaryBody(allResults, postedCount);
        var comments = await _client.Issue.Comment.GetAllForIssue(_context.Owner!, _context.Repo!, _context.PullRequestNumber.Value);
        var existing = comments.FirstOrDefault(comment => comment.Body?.Contains(SummaryMarker, StringComparison.Ordinal) == true);

        if (existing is null)
        {
            await _client.Issue.Comment.Create(_context.Owner!, _context.Repo!, _context.PullRequestNumber.Value, summaryBody);
            return;
        }

        await _client.Issue.Comment.Update(_context.Owner!, _context.Repo!, existing.Id, summaryBody);
    }

    private string BuildSummaryBody(IReadOnlyList<DetectionResult> allResults, int postedCount)
    {
        var suggestionCount = allResults.Count(result => result.Severity == Severity.Suggestion);
        var warningCount = allResults.Count(result => result.Severity == Severity.Warning);
        var errorCount = allResults.Count(result => result.Severity == Severity.Error);
        var byRule = allResults
            .GroupBy(result => new { result.RuleId, result.RuleName })
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key.RuleId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var builder = new StringBuilder();
        builder.AppendLine(SummaryMarker);
        builder.AppendLine("## C# Modernization Analyzer Summary");
        builder.AppendLine();
        builder.AppendLine("| Severity | Count |");
        builder.AppendLine("| --- | ---: |");
        builder.AppendLine($"| Suggestion | {suggestionCount} |");
        builder.AppendLine($"| Warning | {warningCount} |");
        builder.AppendLine($"| Error | {errorCount} |");
        builder.AppendLine();

        if (byRule.Count == 0)
        {
            builder.AppendLine("No modernization opportunities met the configured reporting threshold.");
        }
        else
        {
            builder.AppendLine("| Rule | Finding | Severity | Count |");
            builder.AppendLine("| --- | --- | --- | ---: |");

            foreach (var group in byRule)
            {
                var highestSeverity = group.Max(static result => result.Severity);
                builder.AppendLine($"| {group.Key.RuleId} | {group.Key.RuleName} | {highestSeverity} | {group.Count()} |");
            }
        }

        builder.AppendLine();
        builder.AppendLine($"Posted {postedCount} inline suggestion comment(s) (max {_maxSuggestions}).");
        return builder.ToString();
    }
}
