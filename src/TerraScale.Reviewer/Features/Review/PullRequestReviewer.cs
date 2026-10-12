using System.Text;
using TerraScale.Reviewer.Features.Configuration;
using TerraScale.Reviewer.Features.GitHub;

namespace TerraScale.Reviewer.Features.Review;

/// <summary>
/// Reviews the current head of one pull request and publishes the outcome:
/// the marked summary comment, a review (changes requested for any finding,
/// a comment otherwise) and the <c>ts-reviewer</c> commit status.
/// </summary>
public sealed class PullRequestReviewer(GitHubClient github, ReviewEngine engine, ReviewerOptions options, ILogger<PullRequestReviewer> logger)
{
    public const string StatusContext = "ts-reviewer";

    /// <summary>
    /// Runs one review. <paramref name="cancellation"/> fires when the queue
    /// drops the request or shuts down, which publishes nothing here: the queue
    /// marks a dropped head cancelled once the review stops, and a shutdown leaves
    /// it pending for startup recovery. Individual operations have deadlines;
    /// review size must not truncate model retry budgets.
    /// </summary>
    public Task RunAsync(ReviewRequest request, CancellationToken cancellation) => RunAsync(request, cancellation, null);

    internal async Task RunAsync(ReviewRequest request, CancellationToken cancellation, Func<string, string, ReviewRequest?>? resolveCurrent)
    {
        var ct = cancellation;
        var (owner, name, number) = (request.Owner, request.Name, request.Number);
        var sha = request.HeadSha;
        PullRequest? validated = null;
        var reviewStarted = false;
        try
        {
            if (!await github.IsAllowedAsync(owner, name, ct)) return;
            var pr = await github.PullAsync(owner, name, number, ct) ?? throw new InvalidOperationException("pull request not found");
            if (!ValidTarget(pr, owner, name, number))
                throw new InvalidOperationException("pull request metadata is outside the requested repository boundary");
            validated = pr;
            if (pr.State != "open") { await SkipAsync(request, ReviewQueue.Closed); return; }
            if (sha is not null && !string.Equals(sha, pr.Head.Sha, StringComparison.OrdinalIgnoreCase))
            {
                await SkipAsync(request, ReviewQueue.NewerHead);
                if (resolveCurrent is null) return;
            }
            if (resolveCurrent is not null)
            {
                var resolved = resolveCurrent(pr.Head.Sha, pr.Base.Sha);
                if (resolved is null) return;
                request = resolved;
            }
            sha = pr.Head.Sha;
            // Only this app's outcome for this exact PR and base can suppress a paid review.
            if (!request.Rerun && IsCompleted(await LatestOwnedStatusAsync(pr, owner, name, ct), pr)) return;
            reviewStarted = true;
            logger.LogInformation("review started for {Repository}#{Number}; model selected by workspace monthly bill", owner + "/" + name, number);
            await PublishStatusAsync(owner, name, sha, "pending", StatusDescription(pr.Number, pr.Base.Sha, "Review in progress"));
            await ReviewAsync(pr, owner, name, number, ct);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            logger.LogInformation("review for {Repository}#{Number} was superseded or cancelled", owner + "/" + name, number);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "review failed for {Repository}#{Number}", owner + "/" + name, number);
            // Recovery reconciles unavailable metadata; never overwrite an outcome before paid work begins.
            if (sha is not null && reviewStarted && validated is not null) await PublishStatusAsync(owner, name, sha, "error",
                StatusDescription(number, validated.Base.Sha, "review failed: " + ex.Message));
        }
    }

    internal async Task<ContextStatus?> LatestOwnedStatusAsync(PullRequest pr, string owner, string name, CancellationToken ct) =>
        (await github.StatusesAsync(owner, name, pr.Head.Sha, ct)).FirstOrDefault(x => x.Context == StatusContext
            && string.Equals(x.Creator?.Login, options.BotLogin, StringComparison.OrdinalIgnoreCase)
            && x.Description?.StartsWith(StatusDescription(pr.Number, pr.Base.Sha, string.Empty), StringComparison.Ordinal) == true);

    internal static bool IsCompleted(ContextStatus? status, PullRequest pr) => status?.State is "success" or "failure"
        || status?.State == "error" && status.Description?.StartsWith(StatusDescription(pr.Number, pr.Base.Sha, "Review cancelled:"), StringComparison.Ordinal) != true;

    internal static string StatusDescription(int number, string? baseSha, string detail) =>
        baseSha is null ? detail : $"PR #{number} base {baseSha.ToLowerInvariant()}: {detail}";

    /// <summary>Resolves the pending status of a head whose review the queue dropped.</summary>
    public async Task SkipAsync(ReviewRequest request, string reason)
    {
        if (request.HeadSha is null) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var prefix = request.BaseSha is null ? $"PR #{request.Number} base " : StatusDescription(request.Number, request.BaseSha, string.Empty);
            var latest = (await github.StatusesAsync(request.Owner, request.Name, request.HeadSha, timeout.Token))
                .FirstOrDefault(status => status.Context == StatusContext
                    && string.Equals(status.Creator?.Login, options.BotLogin, StringComparison.OrdinalIgnoreCase)
                    && status.Description?.StartsWith(prefix, StringComparison.Ordinal) == true);
            if (latest?.State is "success" or "failure"
                || latest?.State == "error" && !latest.Description!.Contains(": Review cancelled:", StringComparison.Ordinal)) return;
            await PublishStatusAsync(request.Owner, request.Name, request.HeadSha, "error", StatusDescription(request.Number, request.BaseSha, "Review cancelled: " + reason));
        }
        catch (Exception ex) { logger.LogWarning(ex, "could not resolve cancelled status for {Repository}#{Number}", request.Owner + "/" + request.Name, request.Number); }
    }

    /// <summary>Best-effort commit status publication with its own short deadline.</summary>
    public async Task PublishStatusAsync(string owner, string name, string sha, string state, string description)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await github.StatusAsync(owner, name, sha, new CommitStatus(state, null, description, StatusContext), timeout.Token); }
        catch (Exception ex) { logger.LogWarning(ex, "could not publish {State} review status for {Repository}@{Sha}", state, owner + "/" + name, sha); }
    }

    public static string Format(PullRequest pr, ReviewResult result, string? model = null)
    {
        var header = model is null ? "## ts-reviewer review" : $"## ts-reviewer review · model `{model}`";
        var builder = new StringBuilder($"<!-- ts-reviewer:{pr.Head.Sha} -->\n{header}\n\n### Summary\n\n{result.Summary.Trim()}\n\n");
        builder.Append("Source and diff inspection only; this reviewer did not execute tests or builds.\n\n");
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

    private async Task ReviewAsync(PullRequest pr, string owner, string name, int number, CancellationToken ct)
    {
        ReviewResult result;
        try { result = await engine.RunAsync(pr, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { await PublishStatusAsync(owner, name, pr.Head.Sha, "error", StatusDescription(number, pr.Base.Sha, "review incomplete: " + ex.Message)); return; }
        var latest = await github.PullAsync(owner, name, number, ct);
        if (latest is null || latest.State != "open" || !ValidTarget(latest, owner, name, number)
            || !string.Equals(latest.Head.Sha, pr.Head.Sha, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(latest.Base.Sha, pr.Base.Sha, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("review discarded as stale for {Repository}#{Number}", owner + "/" + name, number);
            await PublishStatusAsync(owner, name, pr.Head.Sha, "error", StatusDescription(number, pr.Base.Sha, "Review cancelled: " + ReviewQueue.NewerHead));
            return;
        }
        var hasFindings = result.Findings.Count > 0;
        var body = Format(pr, result, result.Model ?? ReviewProvider.PrimaryModel);
        var existing = await github.CommentsAsync(owner, name, number, ct);
        if (!await IsCurrentAsync(pr, owner, name, number, ct)) return;
        var marked = existing.FirstOrDefault(x => string.Equals(x.User.Login, options.BotLogin, StringComparison.OrdinalIgnoreCase)
            && x.Body.StartsWith($"<!-- ts-reviewer:{pr.Head.Sha} -->", StringComparison.Ordinal));
        if (marked is not null) await github.UpdateCommentAsync(owner, name, marked.Id, body, ct);
        else await github.CommentAsync(owner, name, number, body, ct);
        var comments = result.Findings
            .Select(f => (f, position: result.Files.FirstOrDefault(x => x.Filename == f.Path) is { } file ? UnifiedDiff.ReviewPosition(file.Patch, f.Line) : 0))
            .Where(x => x.position > 0)
            .Select(x => new ReviewComment($"**{x.f.Severity.Trim().ToUpperInvariant()} · {x.f.Title.Trim()}**\n\n{x.f.Body.Trim()}\n\n**Suggested fix**\n{x.f.Fix.Trim()}", x.f.Path, x.position))
            .ToList();
        var review = hasFindings
            ? new PullReview(body, "REQUEST_CHANGES", pr.Head.Sha, comments)
            : new PullReview(body, "COMMENT", pr.Head.Sha, comments);
        if (!await IsCurrentAsync(pr, owner, name, number, ct)) return;
        await github.ReviewAsync(owner, name, number, review, ct);
        if (!await IsCurrentAsync(pr, owner, name, number, ct)) return;
        if (!hasFindings)
            foreach (var previous in await github.ReviewsAsync(owner, name, number, ct))
            {
                if (previous.State != "CHANGES_REQUESTED" || !string.Equals(previous.User?.Login, options.BotLogin, StringComparison.OrdinalIgnoreCase)) continue;
                if (!await IsCurrentAsync(pr, owner, name, number, ct)) return;
                await github.DismissReviewAsync(owner, name, number, previous.Id,
                    $"Superseded by a complete clean source review of commit {pr.Head.Sha}; no approval or test execution is implied.", ct);
            }
        if (!await IsCurrentAsync(pr, owner, name, number, ct)) return;
        await github.StatusAsync(owner, name, pr.Head.Sha, new CommitStatus(hasFindings ? "failure" : "success", null, StatusDescription(number, pr.Base.Sha, hasFindings ? "Findings require changes" : "No findings"), StatusContext), ct);
        logger.LogInformation("review published for {Repository}#{Number} at {Sha}: {Findings} finding(s)", owner + "/" + name, number, pr.Head.Sha, result.Findings.Count);
    }

    internal static bool ValidTarget(PullRequest pr, string owner, string name, int number) =>
        pr.Number == number && pr.Base?.Repo?.Owner is not null && pr.Head is not null
        && string.Equals(pr.Base.Repo.Owner.Login, owner, StringComparison.OrdinalIgnoreCase)
        && string.Equals(pr.Base.Repo.Name, name, StringComparison.OrdinalIgnoreCase)
        && pr.Base.Sha is { Length: 40 } baseSha && baseSha.All(Uri.IsHexDigit)
        && pr.Head.Sha is { Length: 40 } headSha && headSha.All(Uri.IsHexDigit);

    private async Task<bool> IsCurrentAsync(PullRequest pr, string owner, string name, int number, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var latest = await github.PullAsync(owner, name, number, ct);
        if (latest is not null && latest.State == "open" && ValidTarget(latest, owner, name, number)
            && string.Equals(latest.Head.Sha, pr.Head.Sha, StringComparison.OrdinalIgnoreCase)
            && string.Equals(latest.Base.Sha, pr.Base.Sha, StringComparison.OrdinalIgnoreCase)) return true;
        await SkipAsync(new ReviewRequest(owner, name, number, pr.Head.Sha, BaseSha: pr.Base.Sha), ReviewQueue.NewerHead);
        return false;
    }
}
