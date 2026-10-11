using TerraScale.Reviewer.Features.Configuration;
using TerraScale.Reviewer.Features.GitHub;

namespace TerraScale.Reviewer.Features.Review;

/// <summary>
/// Re-queues reviews that a restart dropped or never received. Queued and
/// running reviews live only in memory, so after a deploy or crash their heads
/// keep a pending <c>ts-reviewer</c> status that nothing will ever resolve, and
/// a push delivered while the service was down leaves its head without any
/// status. On startup this scans the organization's open pull requests and
/// queues every one whose head is pending or was never reviewed. Heads whose
/// review finished (<c>success</c>, <c>failure</c> or <c>error</c>) are left
/// alone: their outcome is published, and re-running a persistently failing
/// review on every restart would loop; a recheck comment retries them.
/// </summary>
public sealed class ReviewRecovery(GitHubClient github, ReviewQueue queue, PullRequestReviewer reviewer, ReviewerOptions options, ILogger<ReviewRecovery> logger) : BackgroundService
{
    private const int PageSize = 50;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var recovered = await RecoverAsync(stoppingToken);
            logger.LogInformation("startup recovery queued {Count} pending or unreviewed pull request(s)", recovered);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex) { logger.LogWarning(ex, "startup recovery of pending reviews failed; re-trigger them with a recheck comment"); }
    }

    internal async Task<int> RecoverAsync(CancellationToken ct)
    {
        var recovered = 0;
        for (var page = 1; ; page++)
        {
            // The reviewer's token carries repository scope only, so the
            // organization's repositories come from repository search.
            var repositories = await github.SearchRepositoriesAsync(page, PageSize, ct);
            foreach (var repository in repositories.Where(x => !x.Archived && string.Equals(x.Owner.Login, options.Organization, StringComparison.OrdinalIgnoreCase)))
                recovered += await RecoverRepositoryAsync(repository.Owner.Login, repository.Name, ct);
            if (repositories.Count < PageSize) return recovered;
        }
    }

    private async Task<int> RecoverRepositoryAsync(string owner, string name, CancellationToken ct)
    {
        var recovered = 0;
        for (var page = 1; ; page++)
        {
            var pulls = await github.OpenPullsAsync(owner, name, page, PageSize, ct);
            foreach (var pull in pulls)
            {
                var status = await github.CombinedStatusAsync(owner, name, pull.Head.Sha, ct);
                var current = status?.Statuses?.FirstOrDefault(x => x.Context == PullRequestReviewer.StatusContext);
                if (current is not null && current.State != "pending") continue;
                logger.LogInformation("recovering review for {Repository}#{Number} at {Head}: {Reason}", owner + "/" + name, pull.Number, pull.Head.Sha, current is null ? "head never reviewed" : "review was pending");
                queue.Enqueue(new ReviewRequest(owner, name, pull.Number, pull.Head.Sha),
                    _ => reviewer.PublishStatusAsync(owner, name, pull.Head.Sha, "pending", "Review queued"));
                recovered++;
            }
            if (pulls.Count < PageSize) return recovered;
        }
    }
}
