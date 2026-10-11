using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.Reviewer.Features.Configuration;
using TerraScale.Reviewer.Features.Commands;
using TerraScale.Reviewer.Features.Context;
using TerraScale.Reviewer.Features.GitHub;
using TerraScale.Reviewer.Features.Review;

namespace TerraScale.Reviewer.Features.Webhooks;

public sealed class WebhookHandler(GitHubClient github, ReviewQueue queue, PullRequestReviewer reviewer, ContextIndexer context, ReviewerOptions options, ILogger<WebhookHandler> logger)
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
        if (!ValidSignature(payload, request.Headers["X-Hub-Signature-256"].ToString(), options.WebhookSecret))
            return Results.Unauthorized();
        var eventName = request.Headers["X-GitHub-Event"].ToString();
        if (eventName is not ("pull_request" or "issue_comment" or "push")) return Results.NoContent();
        var delivery = request.Headers["X-GitHub-Delivery"].ToString();
        if (!Guid.TryParseExact(delivery, "D", out var deliveryId)) return Results.BadRequest("invalid delivery ID");
        delivery = deliveryId.ToString("D");
        WebhookEvent? webhook;
        try { webhook = JsonSerializer.Deserialize<WebhookEvent>(payload); }
        catch (JsonException) { return Results.BadRequest("invalid JSON"); }
        if (webhook?.Repository?.Owner is null || string.IsNullOrWhiteSpace(webhook.Repository.Name)
            || !string.Equals(webhook.Repository.Owner.Login, options.Organization, StringComparison.OrdinalIgnoreCase)) return Results.NoContent();
        if (webhook.Installation is not null && webhook.Installation.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) != options.GitHubInstallationId)
            return Results.NoContent();
        var repository = webhook.Repository;
        var (owner, name) = (repository.Owner.Login, repository.Name);
        // Delivery headers are unsigned; the same command comment cannot buy another review under a new delivery ID.
        var deliveryKey = eventName == "issue_comment" && webhook.Action == "created" && webhook.Comment?.Id is > 0
            ? $"github-comment:{owner.ToLowerInvariant()}/{name.ToLowerInvariant()}:{webhook.Comment.Id}"
            : delivery;
        string? lease = null;
        try
        {
            lease = context.DeliveryStore.TryBeginWebhookDelivery(deliveryKey);
            if (lease is null) return Results.NoContent();
            var result = await DispatchAsync(eventName, webhook, owner, name, cancellationToken);
            if (((IStatusCodeHttpResult)result).StatusCode == StatusCodes.Status202Accepted)
                context.DeliveryStore.CompleteWebhookDelivery(deliveryKey, lease);
            else context.DeliveryStore.ReleaseWebhookDelivery(deliveryKey, lease);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (lease is not null) context.DeliveryStore.ReleaseWebhookDelivery(deliveryKey, lease);
            throw;
        }
        catch (Exception ex)
        {
            if (lease is not null) context.DeliveryStore.ReleaseWebhookDelivery(deliveryKey, lease);
            logger.LogError(ex, "GitHub delivery {Delivery} failed before admission", delivery);
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    private async Task<IResult> DispatchAsync(string eventName, WebhookEvent webhook, string owner, string name, CancellationToken ct)
    {
        if (eventName == "push")
        {
            if (!await github.IsAllowedAsync(owner, name, ct)) return Results.NoContent();
            if (webhook.Ref?.StartsWith("refs/heads/", StringComparison.Ordinal) != true
                || webhook.After is not { Length: 40 } sha || !sha.All(Uri.IsHexDigit) || sha.All(x => x == '0')) return Results.NoContent();
            if (!context.Enqueue(owner, name, sha)) throw new InvalidOperationException("context indexing admission failed");
            return Results.Accepted();
        }
        if (eventName == "pull_request")
        {
            var number = webhook.PullRequest?.Number is > 0 ? webhook.PullRequest.Number : webhook.Number;
            if (number < 1 || webhook.PullRequest is null) return Results.NoContent();
            if (webhook.Action is "opened" or "synchronize" or "reopened")
            {
                QueueReview(new ReviewRequest(owner, name, number, webhook.PullRequest.Head?.Sha, BaseSha: webhook.PullRequest.Base?.Sha));
                _ = AcknowledgeAsync(owner, name, number, null);
                return Results.Accepted();
            }
            if (webhook.Action == "closed")
            {
                _ = queue.CancelWhenClosedAsync(owner, name, number, async () =>
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    return (await github.PullAsync(owner, name, number, timeout.Token))?.State == "closed";
                });
                return Results.Accepted();
            }
            return Results.NoContent();
        }
        if (webhook.Action != "created" || webhook.Issue?.PullRequest is null || webhook.Issue.Number < 1
            || FromBot(webhook) || webhook.Comment?.User?.Login is not { } login) return Results.NoContent();
        var command = ParseCommand(webhook.Comment.Body, options.BotLogin);
        var action = GitHubCommands.Parse(webhook.Comment.Body, options.BotLogin);
        if (command is null && action is null) return Results.NoContent();
        if (!await github.CanReviewAsync(owner, name, login, ct)) return Results.NoContent();
        if (action is { } parsed)
        {
            if (webhook.Comment.CreatedAt is not { } created || created <= DateTimeOffset.UnixEpoch || created > DateTimeOffset.UtcNow.AddMinutes(5)) return Results.BadRequest("invalid command timestamp");
            // Finish an admitted maintainer action even if GitHub stops waiting for this webhook.
            using var commandTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await new GitHubCommands(github, options, context.DeliveryStore).ExecuteAsync(owner, name, webhook.Issue.Number, webhook.Comment.Id, created, parsed, commandTimeout.Token);
            _ = AcknowledgeAsync(owner, name, webhook.Issue.Number, webhook.Comment.Id);
            return Results.Accepted();
        }
        switch (command)
        {
            case "review" or "recheck":
                // Admit before resolving the head so commands retain FIFO order.
                QueueReview(new ReviewRequest(owner, name, webhook.Issue.Number, null, Rerun: true));
                break;
            case "explain":
                await github.CommentAsync(owner, name, webhook.Issue.Number,
                    $"ts-reviewer: the latest evidence and suggested fix are in the marked review comment. Use `@{options.BotLogin} recheck` after pushing a correction.", ct);
                break;
            case "dismiss":
                await github.CommentAsync(owner, name, webhook.Issue.Number,
                    "ts-reviewer: review dismissals use the repository's maintainer workflow; comment commands do not dismiss reviews.", ct);
                break;
        }
        _ = AcknowledgeAsync(owner, name, webhook.Issue.Number, webhook.Comment.Id);
        return Results.Accepted();
    }

    public static bool ValidSignature(ReadOnlySpan<byte> body, string signature, string secret)
    {
        if (string.IsNullOrWhiteSpace(secret) || signature.Length != 71 || !signature.StartsWith("sha256=", StringComparison.Ordinal)) return false;
        try
        {
            var expected = Convert.FromHexString(signature[7..]);
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
            if (string.Equals(words[i], mention, StringComparison.OrdinalIgnoreCase) && words[i + 1].ToLowerInvariant() is "review" or "recheck" or "explain" or "dismiss") return words[i + 1].ToLowerInvariant();
        return null;
    }

    private bool FromBot(WebhookEvent webhook) => webhook.Comment?.User?.Type == "Bot" || webhook.Sender?.Type == "Bot"
        || string.Equals(webhook.Comment?.User?.Login, options.BotLogin, StringComparison.OrdinalIgnoreCase)
        || string.Equals(webhook.Sender?.Login, options.BotLogin, StringComparison.OrdinalIgnoreCase);

    private async Task AcknowledgeAsync(string owner, string name, int number, long? commentId)
    {
        try
        {
            if (commentId is > 0) await github.ReactToCommentAsync(owner, name, commentId.Value, CancellationToken.None);
            else await github.ReactToPullRequestAsync(owner, name, number, CancellationToken.None);
        }
        catch (Exception ex) { logger.LogWarning(ex, "failed to acknowledge {Repository}#{Number}", owner + "/" + name, number); }
    }

    private void QueueReview(ReviewRequest request)
    {
        request = request with { HeadSha = string.IsNullOrWhiteSpace(request.HeadSha) ? null : request.HeadSha };
        if (!request.Rerun) { queue.Enqueue(request); return; }
        queue.Enqueue(request, async admitted =>
        {
            if (admitted.HeadSha is null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var pull = await github.PullAsync(admitted.Owner, admitted.Name, admitted.Number, timeout.Token);
                if (pull?.State != "open" || string.IsNullOrWhiteSpace(pull.Head.Sha))
                { queue.Cancel(admitted.Owner, admitted.Name, admitted.Number); return; }
                var resolved = queue.ResolveHead(admitted, pull.Head.Sha, pull.Base.Sha);
                if (resolved is null) return;
                admitted = resolved;
            }
            await reviewer.PublishStatusAsync(admitted.Owner, admitted.Name, admitted.HeadSha!, "pending",
                PullRequestReviewer.StatusDescription(admitted.Number, admitted.BaseSha, "Review queued"));
        });
    }

    private sealed record WebhookEvent([property: JsonPropertyName("action")] string? Action, [property: JsonPropertyName("number")] int Number, [property: JsonPropertyName("ref")] string? Ref, [property: JsonPropertyName("after")] string? After, [property: JsonPropertyName("repository")] WebhookRepository? Repository, [property: JsonPropertyName("pull_request")] WebhookPullRequest? PullRequest, [property: JsonPropertyName("issue")] WebhookIssue? Issue, [property: JsonPropertyName("comment")] WebhookComment? Comment, [property: JsonPropertyName("sender")] Owner? Sender, [property: JsonPropertyName("installation")] WebhookInstallation? Installation);
    private sealed record WebhookInstallation([property: JsonPropertyName("id")] long Id);
    private sealed record WebhookRepository([property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("owner")] Owner Owner);
    private sealed record WebhookPullRequest([property: JsonPropertyName("number")] int Number, [property: JsonPropertyName("head")] WebhookBranch? Head,
        [property: JsonPropertyName("base")] WebhookBranch? Base);
    private sealed record WebhookIssue([property: JsonPropertyName("number")] int Number, [property: JsonPropertyName("pull_request")] WebhookIssuePullRequest? PullRequest);
    private sealed record WebhookIssuePullRequest;
    private sealed record WebhookBranch([property: JsonPropertyName("sha")] string? Sha);
    private sealed record WebhookComment([property: JsonPropertyName("id")] long Id, [property: JsonPropertyName("body")] string Body, [property: JsonPropertyName("user")] Owner? User,
        [property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt = null);
}
