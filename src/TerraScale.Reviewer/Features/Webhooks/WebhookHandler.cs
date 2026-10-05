using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.Reviewer.Features.Configuration;
using TerraScale.Reviewer.Features.Context;
using TerraScale.Reviewer.Features.Forgejo;
using TerraScale.Reviewer.Features.Review;

namespace TerraScale.Reviewer.Features.Webhooks;

public sealed class WebhookHandler(ForgejoClient forgejo, ReviewQueue queue, PullRequestReviewer reviewer, ContextIndexer context, ReviewerOptions options, ILogger<WebhookHandler> logger)
{
    private const int MaxPayloadBytes = 1 << 20;

    public async Task<IResult> HandleAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (!HttpMethods.IsPost(request.Method)) return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);
        if (request.ContentLength > MaxPayloadBytes) return Results.BadRequest("invalid payload");
        using var body = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (body.Length <= MaxPayloadBytes)
        {
            var read = await request.Body.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            await body.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        if (body.Length > MaxPayloadBytes) return Results.BadRequest("invalid payload");
        var payload = body.ToArray();
        if (!ValidSignature(payload, request.Headers["X-Forgejo-Signature"].ToString(), options.WebhookSecret))
            return Results.Unauthorized();
        WebhookEvent? webhook;
        try { webhook = JsonSerializer.Deserialize<WebhookEvent>(payload); }
        catch (JsonException) { return Results.BadRequest("invalid JSON"); }
        if (webhook is null || webhook.Repository is null || webhook.Repository.Owner is null ||
            !string.Equals(webhook.Repository.Owner.Login, options.Organization, StringComparison.OrdinalIgnoreCase)) return Results.NoContent();
        var repository = webhook.Repository;
        var (owner, name) = (repository.Owner.Login, repository.Name);
        if (!string.IsNullOrWhiteSpace(webhook.After) && !webhook.After.All(x => x == '0') && webhook.Ref?.StartsWith("refs/heads/", StringComparison.Ordinal) == true)
        {
            var queued = context.Enqueue(owner, name, webhook.After);
            return queued ? Results.Accepted() : Results.BadRequest("invalid commit");
        }
        var number = webhook.Number != 0 ? webhook.Number : webhook.PullRequest?.Number ?? 0;
        if (webhook.Action == "created" && webhook.PullRequest is null && webhook.Issue?.PullRequest is null)
            return Results.NoContent();
        if (number == 0 && webhook.Issue?.PullRequest is not null) number = webhook.Issue.Number;
        if (number < 1) return Results.NoContent();
        var head = webhook.PullRequest?.Head?.Sha;
        if (webhook.Action is "opened" or "synchronized" or "reopened")
        {
            _ = AcknowledgeAsync(owner, name, number, null);
            _ = QueueReviewAsync(new ReviewRequest(owner, name, number, head));
        }
        else if (webhook.Action == "closed")
        {
            queue.Cancel(owner, name, number);
        }
        else if (webhook.Action == "created" && !FromBot(webhook) && ParseCommand(webhook.Comment?.Body ?? string.Empty, options.BotLogin) is { } command)
        {
            _ = AcknowledgeAsync(owner, name, number, webhook.Comment?.Id);
            _ = RunCommandAsync(owner, name, number, command, head);
        }
        else return Results.NoContent();
        return Results.Accepted();
    }

    public static bool ValidSignature(ReadOnlySpan<byte> body, string signature, string secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) return false;
        signature = signature.StartsWith("sha256=", StringComparison.Ordinal) ? signature[7..] : signature;
        try
        {
            var expected = Convert.FromHexString(signature);
            var computed = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
            return CryptographicOperations.FixedTimeEquals(expected, computed);
        }
        catch (FormatException) { return false; }
    }

    public static string? ParseCommand(string body, string bot)
    {
        var words = body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var mention = "@" + bot.ToLowerInvariant();
        for (var i = 0; i + 1 < words.Length; i++)
            if (string.Equals(words[i], mention, StringComparison.OrdinalIgnoreCase) && words[i + 1].ToLowerInvariant() is "review" or "recheck" or "explain" or "dismiss" or "escalate") return words[i + 1].ToLowerInvariant();
        return null;
    }

    // The bot's own summary quotes model output, which can mention the bot;
    // its comments must never trigger another review.
    private bool FromBot(WebhookEvent webhook) =>
        string.Equals(webhook.Comment?.User?.Login ?? webhook.Sender?.Login, options.BotLogin, StringComparison.OrdinalIgnoreCase);

    /// <summary>Adds a 👀 reaction to the pull request or the triggering comment so users see the bot picked it up.</summary>
    private async Task AcknowledgeAsync(string owner, string name, int number, long? commentId)
    {
        try
        {
            if (commentId is > 0) await forgejo.ReactToCommentAsync(owner, name, commentId.Value, CancellationToken.None);
            else await forgejo.ReactToPullRequestAsync(owner, name, number, CancellationToken.None);
        }
        catch (Exception ex) { logger.LogWarning(ex, "failed to acknowledge {Repository}#{Number}", owner + "/" + name, number); }
    }

    /// <summary>Resolves the head when the event did not carry it, marks it queued and hands the request to the queue.</summary>
    private async Task QueueReviewAsync(ReviewRequest request)
    {
        try
        {
            var sha = request.HeadSha;
            if (string.IsNullOrWhiteSpace(sha))
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try { sha = (await forgejo.PullAsync(request.Owner, request.Name, request.Number, timeout.Token))?.Head.Sha; }
                catch (Exception ex) { logger.LogWarning(ex, "could not load {Repository}#{Number} before queueing", request.Owner + "/" + request.Name, request.Number); }
            }
            request = request with { HeadSha = string.IsNullOrWhiteSpace(sha) ? null : sha };
            if (queue.Enqueue(request) != ReviewAdmission.Duplicate && request.HeadSha is not null)
                await reviewer.PublishStatusAsync(request.Owner, request.Name, request.HeadSha, "pending", "Review queued");
        }
        catch (Exception ex) { logger.LogError(ex, "could not queue review for {PullRequest}", request.Key); }
    }

    private async Task RunCommandAsync(string owner, string name, int number, string command, string? head)
    {
        try
        {
            switch (command)
            {
                case "review" or "recheck":
                    await QueueReviewAsync(new ReviewRequest(owner, name, number, head));
                    break;
                case "escalate":
                    await QueueReviewAsync(new ReviewRequest(owner, name, number, head, options.EscalationModel));
                    break;
                case "explain":
                    await forgejo.CommentAsync(owner, name, number, $"ts-reviewer: the latest evidence and suggested fix are in the marked review comment. Use `@{options.BotLogin} recheck` after pushing a correction or `@{options.BotLogin} escalate` to re-review with {options.EscalationModel}.", CancellationToken.None);
                    break;
                case "dismiss":
                    await forgejo.CommentAsync(owner, name, number, "ts-reviewer: dismissals require a maintainer workflow and are not accepted from an unverified comment.", CancellationToken.None);
                    break;
            }
        }
        catch (Exception ex) { logger.LogError(ex, "command {Command} failed for {Repository}#{Number}", command, owner + "/" + name, number); }
    }

    private sealed record WebhookEvent([property: JsonPropertyName("action")] string? Action, [property: JsonPropertyName("number")] int Number, [property: JsonPropertyName("ref")] string? Ref, [property: JsonPropertyName("after")] string? After, [property: JsonPropertyName("repository")] WebhookRepository? Repository, [property: JsonPropertyName("pull_request")] WebhookPullRequest? PullRequest, [property: JsonPropertyName("issue")] WebhookIssue? Issue, [property: JsonPropertyName("comment")] WebhookComment? Comment, [property: JsonPropertyName("sender")] Owner? Sender);
    private sealed record WebhookRepository([property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("owner")] Owner Owner);
    private sealed record WebhookPullRequest([property: JsonPropertyName("number")] int Number, [property: JsonPropertyName("head")] WebhookBranch? Head);
    private sealed record WebhookIssue([property: JsonPropertyName("number")] int Number, [property: JsonPropertyName("pull_request")] WebhookIssuePullRequest? PullRequest);
    private sealed record WebhookIssuePullRequest;
    private sealed record WebhookBranch([property: JsonPropertyName("sha")] string? Sha);
    private sealed record WebhookComment([property: JsonPropertyName("id")] long Id, [property: JsonPropertyName("body")] string Body, [property: JsonPropertyName("user")] Owner? User);
}
