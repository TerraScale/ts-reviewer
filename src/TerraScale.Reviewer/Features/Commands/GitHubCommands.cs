using TerraScale.Reviewer.Features.Configuration;
using TerraScale.Reviewer.Features.Context;
using TerraScale.Reviewer.Features.GitHub;
using TerraScale.Reviewer.Features.Review;

namespace TerraScale.Reviewer.Features.Commands;

internal sealed class GitHubCommands(GitHubClient github, ReviewerOptions options, ContextStore store)
{
    internal static (string Verb, string Argument)? Parse(string body, string bot)
    {
        var text = body.Trim();
        if (text.Contains('\n') || text.Contains('\r')) return null;
        var words = text.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length != 3 || !string.Equals(words[0], "@" + bot, StringComparison.OrdinalIgnoreCase)) return null;
        return words[1].ToLowerInvariant() switch
        {
            "issue" when words[2].Length is > 0 and <= 120 && !words[2].Any(char.IsControl) => ("issue", words[2]),
            "link" when words[2].StartsWith('#') && int.TryParse(words[2][1..], out var number) && number > 0 => ("link", number.ToString()),
            "workflow" when words[2] == "ci" => ("workflow", "ci"),
            _ => null
        };
    }

    internal async Task ExecuteAsync(string owner, string name, int number, long commentId, DateTimeOffset commentCreatedAt, (string Verb, string Argument) command, CancellationToken ct)
    {
        if (!await github.IsAllowedAsync(owner, name, ct)) return;
        var pr = await github.PullAsync(owner, name, number, ct);
        if (pr is null || pr.State != "open" || !PullRequestReviewer.ValidTarget(pr, owner, name, number)) return;
        var repositoryUrl = $"https://github.com/{owner}/{name}";
        var marker = $"<!-- ts-reviewer-command:{number}:{commentId} -->";
        async Task CommentAsync(string body)
        {
            using var feedback = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var existing = (await github.CommentsAsync(owner, name, number, feedback.Token)).FirstOrDefault(comment =>
                string.Equals(comment.User?.Login, options.BotLogin, StringComparison.OrdinalIgnoreCase)
                && comment.Body?.StartsWith(marker, StringComparison.Ordinal) == true);
            if (existing is null) await github.CommentAsync(owner, name, number, marker + "\n" + body, feedback.Token);
            else await github.UpdateCommentAsync(owner, name, existing.Id, marker + "\n" + body, feedback.Token);
        }
        async Task<bool> CurrentAsync()
        {
            var latest = await github.PullAsync(owner, name, number, ct);
            return latest is not null && latest.State == "open" && PullRequestReviewer.ValidTarget(latest, owner, name, number)
                && latest.Head.Sha == pr.Head.Sha && latest.Base.Sha == pr.Base.Sha;
        }
        switch (command.Verb)
        {
            case "link":
                var linked = await github.IssueAsync(owner, name, int.Parse(command.Argument), ct);
                if (linked is not null && await CurrentAsync())
                    await CommentAsync($"Related issue: {repositoryUrl}/issues/{linked.Number}. PR head: `{pr.Head.Sha}`.");
                break;
            case "issue":
                var review = (await github.ReviewsAsync(owner, name, number, ct)).LastOrDefault(review =>
                    string.Equals(review.User?.Login, options.BotLogin, StringComparison.OrdinalIgnoreCase)
                    && review.CommitId == pr.Head.Sha && review.Body?.StartsWith($"<!-- ts-reviewer:{pr.Head.Sha} -->", StringComparison.Ordinal) == true);
                if (review is null)
                {
                    await CommentAsync($"No completed ts-reviewer review exists for `{pr.Head.Sha}`. Run `@{options.BotLogin} recheck` first.");
                    return;
                }
                // GitHub issue creation has no idempotency key; recover an ambiguous POST by its owned marker.
                var issue = (await github.IssuesAsync(owner, name, ct, commentCreatedAt - TimeSpan.FromMinutes(5))).FirstOrDefault(issue =>
                    string.Equals(issue.User?.Login, options.BotLogin, StringComparison.OrdinalIgnoreCase)
                    && issue.Body?.StartsWith(marker, StringComparison.Ordinal) == true);
                if (!await CurrentAsync()) return;
                issue ??= await github.CreateIssueAsync(owner, name, command.Argument,
                    $"{marker}\nFollow-up to {repositoryUrl}/pull/{number}, exact reviewed commit {repositoryUrl}/commit/{pr.Head.Sha}.\nFull review: {repositoryUrl}/pull/{number}#pullrequestreview-{review.Id}\n\n{Excerpt(review.Body)}", ct);
                if (await CurrentAsync()) await CommentAsync($"Follow-up issue: {repositoryUrl}/issues/{issue.Number}. The issue identifies the inspected commit.");
                break;
            case "workflow":
                if (!string.Equals(owner, "TerraScale", StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(name, "ts-reviewer", StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(pr.Head.Repo?.Owner?.Login, owner, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(pr.Head.Repo?.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    await CommentAsync("Workflow commands are restricted to same-repository ts-reviewer PRs. Fork code is reviewed without dispatching workflows.");
                    return;
                }
                var repository = await github.RepoAsync(owner, name, ct);
                if (repository?.DefaultBranch != "main") throw new InvalidOperationException("trusted CI requires the main default branch");
                var workflow = await github.WorkflowAsync(owner, name, "ci.yml", ct);
                if (workflow?.Path != ".github/workflows/ci.yml" || workflow.State != "active") throw new InvalidOperationException("trusted CI workflow is unavailable");
                var sha = await github.BranchAsync(owner, name, repository.DefaultBranch, ct);
                if (sha is not { Length: 40 } || !sha.All(Uri.IsHexDigit)) throw new InvalidOperationException("trusted CI commit is unavailable");
                if (!await CurrentAsync()) return;
                var receipt = $"github-workflow-command:{owner.ToLowerInvariant()}/{name.ToLowerInvariant()}:{commentId}";
                var lease = store.TryBeginWebhookDelivery(receipt);
                if (lease is null) return;
                // Dispatch has no idempotency key: an uncertain outcome requires a new human command, never replay.
                var sent = false;
                string message;
                try
                {
                    await github.DispatchWorkflowAsync(owner, name, "ci.yml", repository.DefaultBranch, sha, ct, () =>
                    {
                        store.CompleteWebhookDelivery(receipt, lease);
                        sent = true;
                    });
                    message = $"CI requested for trusted `main` at `{sha}`. This command does not deploy. PR-head checks run through the normal pull-request event.";
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException)
                {
                    if (!sent) store.ReleaseWebhookDelivery(receipt, lease);
                    message = sent ? "CI dispatch outcome is unconfirmed. Inspect Actions before issuing a new command; this comment will not dispatch again."
                        : "CI was not dispatched because authentication or request preparation failed. Resolve the setup failure before issuing a new command.";
                }
                await CommentAsync($"{message}\n{repositoryUrl}/actions/workflows/ci.yml");
                break;
        }
    }
    private static string Excerpt(string body)
    {
        if (body.Length <= 60_000) return body;
        var end = char.IsHighSurrogate(body[59_999]) ? 59_999 : 60_000;
        return body[..end] + "\n\nReview excerpt truncated; the complete review is linked above.";
    }
}
