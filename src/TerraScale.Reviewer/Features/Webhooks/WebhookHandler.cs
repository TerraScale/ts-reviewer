using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.Reviewer.Features.Configuration;
using TerraScale.Reviewer.Features.Context;
using TerraScale.Reviewer.Features.Forgejo;
using TerraScale.Reviewer.Features.Review;

namespace TerraScale.Reviewer.Features.Webhooks;

public sealed class WebhookHandler(ForgejoClient forgejo, ReviewEngine engine, ContextIndexer context, ReviewerOptions options, ILogger<WebhookHandler> logger)
{
    private const int MaxPayloadBytes = 1 << 20;
    private readonly SemaphoreSlim _slots = new(options.MaxConcurrent, options.MaxConcurrent);

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
        if (!string.IsNullOrWhiteSpace(webhook.After) && !webhook.After.All(x => x == '0') && webhook.Ref?.StartsWith("refs/heads/", StringComparison.Ordinal) == true)
        {
            var queued = context.Enqueue(repository.Owner.Login, repository.Name, webhook.After);
            return queued ? Results.Accepted() : Results.BadRequest("invalid commit");
        }
        var number = webhook.Number != 0 ? webhook.Number : webhook.PullRequest?.Number ?? 0;
        if (number < 1) return Results.NoContent();
        if (webhook.Action is "opened" or "synchronize" or "reopened")
        {
            _ = AcknowledgeAsync(repository.Owner.Login, repository.Name, number, null);
            _ = RunBoundedAsync(repository.Owner.Login, repository.Name, number);
        }
        else if (webhook.Action == "created" && ParseCommand(webhook.Comment?.Body ?? string.Empty, options.BotLogin) is { } command)
        {
            _ = AcknowledgeAsync(repository.Owner.Login, repository.Name, number, webhook.Comment?.Id);
            _ = RunCommandAsync(repository.Owner.Login, repository.Name, number, command);
        }
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

    public static string Format(PullRequest pr, ReviewResult result, string? model = null)
    {
        var header = model is null ? "## ts-reviewer review" : $"## ts-reviewer review · model `{model}`";
        var builder = new StringBuilder($"<!-- ts-reviewer:{pr.Head.Sha} -->\n{header}\n\n### Summary\n\n{result.Summary.Trim()}\n\n");
        if (result.Findings.Count == 0) return builder.Append("### Result\n\n✅ No evidence-backed findings met the reporting threshold.\n").ToString();

        builder.Append("### Findings (").Append(result.Findings.Count).Append(")\n\n");
        foreach (var finding in result.Findings)
        {
            builder.Append("#### ").Append(finding.Severity.Trim().ToUpperInvariant()).Append(" · ").Append(finding.Title.Trim()).Append("\n\n")
                .Append('`').Append(finding.Path).Append(':').Append(finding.Line).Append("`\n\n")
                .Append(finding.Body.Trim()).Append("\n\n")
                .Append("<details>\n<summary>Suggested fix</summary>\n\n")
                .Append(finding.Fix.Trim()).Append("\n\n</details>\n\n");
        }
        return builder.ToString();
    }

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

    private async Task RunBoundedAsync(string owner, string name, int number, string? model = null)
    {
        await _slots.WaitAsync();
        try { await ReviewAsync(owner, name, number, model); }
        catch (Exception ex) { logger.LogError(ex, "review failed for {Repository}#{Number}", owner + "/" + name, number); }
        finally { _slots.Release(); }
    }
    private async Task ReviewAsync(string owner, string name, int number, string? model = null)
    {
        using var timeout = new CancellationTokenSource(options.ReviewTimeout);
        var ct = timeout.Token;
        logger.LogInformation("review started for {Repository}#{Number} with model {Model}", owner + "/" + name, number, model ?? options.Model);
        var pr = await forgejo.PullAsync(owner, name, number, ct) ?? throw new InvalidOperationException("pull request not found");
        var files = await forgejo.FilesAsync(owner, name, number, ct);
        string raw;
        try { raw = await forgejo.DiffAsync(owner, name, number, ct); }
        catch (Exception ex) { await FailStatusAsync(owner, name, pr.Head.Sha, "could not retrieve patch: " + ex.Message); return; }
        var parsed = UnifiedDiff.Parse(raw);
        if (parsed.Count > 0) files = parsed;
        ReviewResult result;
        try { result = await engine.RunAsync(pr, ct, model); }
        catch (Exception ex) { await FailStatusAsync(owner, name, pr.Head.Sha, "review incomplete: " + ex.Message); return; }
        var latest = await forgejo.PullAsync(owner, name, number, ct);
        if (latest is null || !string.Equals(latest.Head.Sha, pr.Head.Sha, StringComparison.OrdinalIgnoreCase)) { logger.LogInformation("review discarded as stale for {Repository}#{Number}", owner + "/" + name, number); return; }
        var critical = result.Findings.Any(x => x.Severity is "critical" or "high");
        var body = Format(pr, result, model ?? options.Model);
        var existing = await forgejo.CommentsAsync(owner, name, number, ct);
        var updated = false;
        foreach (var comment in existing.Where(x => string.Equals(x.User.Login, options.BotLogin, StringComparison.OrdinalIgnoreCase) && x.Body.StartsWith("<!-- ts-reviewer:", StringComparison.Ordinal)))
        { await forgejo.UpdateCommentAsync(owner, name, comment.Id, body, ct); updated = true; break; }
        if (!updated) await forgejo.CommentAsync(owner, name, number, body, ct);
        var comments = result.Findings
            .Select(f => (f, position: files.FirstOrDefault(x => x.Filename == f.Path) is { } file ? UnifiedDiff.ReviewPosition(file.Patch, f.Line) : 0))
            .Where(x => x.position > 0)
            .Select(x => new ReviewComment($"**{x.f.Severity.Trim().ToUpperInvariant()} · {x.f.Title.Trim()}**\n\n{x.f.Body.Trim()}\n\n**Suggested fix**\n{x.f.Fix.Trim()}", x.f.Path, x.position))
            .ToList();
        if (comments.Count > 0) await forgejo.ReviewAsync(owner, name, number, new PullReview("ts-reviewer findings are documented above.", critical ? "REQUEST_CHANGES" : "APPROVE", pr.Head.Sha, comments), ct);
        await forgejo.StatusAsync(owner, name, pr.Head.Sha, new CommitStatus(critical ? "failure" : "success", null, critical ? "Critical/high findings require changes" : "No critical or high findings", "ts-reviewer"), ct);
    }
    private async Task FailStatusAsync(string owner, string name, string sha, string description)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); try { await forgejo.StatusAsync(owner, name, sha, new CommitStatus("error", null, description, "ts-reviewer"), timeout.Token); } catch (Exception ex) { logger.LogError(ex, "publish failed review status"); } }
    private async Task RunCommandAsync(string owner, string name, int number, string command)
    { try { if (command is "review" or "recheck") await RunBoundedAsync(owner, name, number); else if (command == "escalate") await RunBoundedAsync(owner, name, number, options.EscalationModel); else if (command == "explain") await forgejo.CommentAsync(owner, name, number, $"ts-reviewer: the latest evidence and suggested fix are in the marked review comment. Use `@{options.BotLogin} recheck` after pushing a correction or `@{options.BotLogin} escalate` to re-review with {options.EscalationModel}.", CancellationToken.None); else if (command == "dismiss") await forgejo.CommentAsync(owner, name, number, "ts-reviewer: dismissals require a maintainer workflow and are not accepted from an unverified comment.", CancellationToken.None); } catch (Exception ex) { logger.LogError(ex, "command failed"); } }

    private sealed record WebhookEvent([property: JsonPropertyName("action")] string? Action, [property: JsonPropertyName("number")] int Number, [property: JsonPropertyName("ref")] string? Ref, [property: JsonPropertyName("after")] string? After, [property: JsonPropertyName("repository")] WebhookRepository? Repository, [property: JsonPropertyName("pull_request")] WebhookPullRequest? PullRequest, [property: JsonPropertyName("comment")] WebhookComment? Comment);
    private sealed record WebhookRepository([property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("owner")] Owner Owner);
    private sealed record WebhookPullRequest([property: JsonPropertyName("number")] int Number);
    private sealed record WebhookComment([property: JsonPropertyName("id")] long Id, [property: JsonPropertyName("body")] string Body);
}
