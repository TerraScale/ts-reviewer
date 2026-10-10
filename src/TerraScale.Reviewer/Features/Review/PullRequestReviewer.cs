using System.Text;
using TerraScale.Reviewer.Features.Configuration;
using TerraScale.Reviewer.Features.Forgejo;

namespace TerraScale.Reviewer.Features.Review;

/// <summary>
/// Reviews the current head of one pull request and publishes the outcome:
/// the marked summary comment, a review (changes requested for any finding,
/// approved otherwise) and the <c>ts-reviewer</c> commit status.
/// </summary>
public sealed class PullRequestReviewer(ForgejoClient forgejo, ReviewEngine engine, ReviewerOptions options, ILogger<PullRequestReviewer> logger)
{
    public const string StatusContext = "ts-reviewer";

    /// <summary>
    /// Runs one review. <paramref name="cancellation"/> fires when the queue
    /// drops the request or shuts down, which publishes nothing here: the queue
    /// marks a dropped head skipped once the review stops, and a shutdown leaves
    /// it pending for startup recovery. The review deadline is separate and
    /// reports a timed-out review on the head.
    /// </summary>
    public async Task RunAsync(ReviewRequest request, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(options.ReviewTimeout);
        var ct = deadline.Token;
        var (owner, name, number) = (request.Owner, request.Name, request.Number);
        var sha = request.HeadSha;
        try
        {
            logger.LogInformation("review started for {Repository}#{Number} with model {Model}", owner + "/" + name, number, ReviewProvider.PrimaryModel);
            var pr = await forgejo.PullAsync(owner, name, number, ct) ?? throw new InvalidOperationException("pull request not found");
            if (sha is not null && !string.Equals(sha, pr.Head.Sha, StringComparison.OrdinalIgnoreCase))
            {
                await SkipAsync(request, ReviewQueue.NewerHead);
                return;
            }
            sha = pr.Head.Sha;
            await PublishStatusAsync(owner, name, sha, "pending", "Review in progress");
            await ReviewAsync(pr, owner, name, number, ct);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            logger.LogInformation("review for {Repository}#{Number} was superseded or cancelled", owner + "/" + name, number);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            logger.LogWarning("review for {Repository}#{Number} timed out after {Timeout}", owner + "/" + name, number, options.ReviewTimeout);
            if (sha is not null) await PublishStatusAsync(owner, name, sha, "error", $"review timed out after {options.ReviewTimeout} — raise TS_REVIEWER_TIMEOUT if this recurs");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "review failed for {Repository}#{Number}", owner + "/" + name, number);
            if (sha is not null) await PublishStatusAsync(owner, name, sha, "error", "review failed: " + ex.Message);
        }
    }

    /// <summary>Resolves the pending status of a head whose review the queue dropped.</summary>
    public Task SkipAsync(ReviewRequest request, string reason) =>
        request.HeadSha is null ? Task.CompletedTask : PublishStatusAsync(request.Owner, request.Name, request.HeadSha, "skipped", "Review skipped: " + reason);

    /// <summary>Best-effort commit status publication with its own short deadline.</summary>
    public async Task PublishStatusAsync(string owner, string name, string sha, string state, string description)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await forgejo.StatusAsync(owner, name, sha, new CommitStatus(state, null, description, StatusContext), timeout.Token); }
        catch (Exception ex) { logger.LogWarning(ex, "could not publish {State} review status for {Repository}@{Sha}", state, owner + "/" + name, sha); }
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

    private async Task ReviewAsync(PullRequest pr, string owner, string name, int number, CancellationToken ct)
    {
        ReviewResult result;
        try { result = await engine.RunAsync(pr, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { await PublishStatusAsync(owner, name, pr.Head.Sha, "error", "review incomplete: " + ex.Message); return; }
        var latest = await forgejo.PullAsync(owner, name, number, ct);
        if (latest is null || !string.Equals(latest.Head.Sha, pr.Head.Sha, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("review discarded as stale for {Repository}#{Number}", owner + "/" + name, number);
            await PublishStatusAsync(owner, name, pr.Head.Sha, "skipped", "Review skipped: " + ReviewQueue.NewerHead);
            return;
        }
        var hasFindings = result.Findings.Count > 0;
        var body = Format(pr, result, result.Model ?? ReviewProvider.PrimaryModel);
        var existing = await forgejo.CommentsAsync(owner, name, number, ct);
        var marked = existing.FirstOrDefault(x => string.Equals(x.User.Login, options.BotLogin, StringComparison.OrdinalIgnoreCase) && x.Body.StartsWith("<!-- ts-reviewer:", StringComparison.Ordinal));
        if (marked is not null) await forgejo.UpdateCommentAsync(owner, name, marked.Id, body, ct);
        else await forgejo.CommentAsync(owner, name, number, body, ct);
        var comments = result.Findings
            .Select(f => (f, position: result.Files.FirstOrDefault(x => x.Filename == f.Path) is { } file ? UnifiedDiff.ReviewPosition(file.Patch, f.Line) : 0))
            .Where(x => x.position > 0)
            .Select(x => new ReviewComment($"**{x.f.Severity.Trim().ToUpperInvariant()} · {x.f.Title.Trim()}**\n\n{x.f.Body.Trim()}\n\n**Suggested fix**\n{x.f.Fix.Trim()}", x.f.Path, x.position))
            .ToList();
        // Clean reviews are submitted as approvals so branch protection that
        // requires an approving review can merge on a passing ts-reviewer run.
        var review = hasFindings
            ? new PullReview("ts-reviewer findings are documented above.", "REQUEST_CHANGES", pr.Head.Sha, comments)
            : new PullReview("ts-reviewer found no issues.", "APPROVED", pr.Head.Sha, comments);
        try { await forgejo.ReviewAsync(owner, name, number, review, ct); }
        catch (HttpRequestException ex) when (!hasFindings)
        {
            // Forgejo rejects approvals from the pull request author (e.g. a
            // bot-authored PR); a clean review must still publish success.
            logger.LogWarning(ex, "approval rejected for {Repository}#{Number}", owner + "/" + name, number);
        }
        await forgejo.StatusAsync(owner, name, pr.Head.Sha, new CommitStatus(hasFindings ? "failure" : "success", null, hasFindings ? "Findings require changes" : "No findings", StatusContext), ct);
        logger.LogInformation("review published for {Repository}#{Number} at {Sha}: {Findings} finding(s)", owner + "/" + name, number, pr.Head.Sha, result.Findings.Count);
    }
}
