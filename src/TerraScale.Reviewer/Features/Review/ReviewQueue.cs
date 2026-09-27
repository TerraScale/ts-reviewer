using System.Threading.Channels;
using TerraScale.Reviewer.Features.Configuration;

namespace TerraScale.Reviewer.Features.Review;

/// <summary>A request to review the current head of one pull request.</summary>
/// <param name="HeadSha">Head the request was raised for, when known. Reviews always run on the head at run time; the SHA only drives deduplication.</param>
/// <param name="Model">Model override (escalation), or <c>null</c> for the default model.</param>
public sealed record ReviewRequest(string Owner, string Name, int Number, string? HeadSha, string? Model = null)
{
    public string Key => $"{Owner}/{Name}#{Number}".ToLowerInvariant();
}

/// <summary>What the queue did with an enqueued request.</summary>
public enum ReviewAdmission
{
    /// <summary>The request was queued behind no other request for the pull request.</summary>
    Queued,
    /// <summary>The request replaced an older request that was still waiting for a worker.</summary>
    Superseded,
    /// <summary>The same head and model is already being reviewed; the request was dropped.</summary>
    Duplicate,
}

/// <summary>
/// Review queue with at most one waiting and one running review per pull
/// request. A newer request replaces the one still waiting, a request for a
/// new head cancels the running review of the old head (its result would be
/// discarded as stale anyway), and a request for the head already under review
/// is dropped. A fixed pool of workers drains the queue in arrival order, so a
/// slot is never leaked and waiting work never expires.
/// </summary>
public sealed class ReviewQueue : BackgroundService
{
    private readonly Func<ReviewRequest, CancellationToken, Task> _runner;
    private readonly int _workers;
    private readonly ILogger<ReviewQueue> _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, PullRequestSlot> _slots = new(StringComparer.Ordinal);
    private readonly Channel<string> _ready = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { AllowSynchronousContinuations = false });

    public ReviewQueue(PullRequestReviewer reviewer, ReviewerOptions options, ILogger<ReviewQueue> logger)
        : this(reviewer.RunAsync, options.MaxConcurrent, logger) { }

    internal ReviewQueue(Func<ReviewRequest, CancellationToken, Task> runner, int workers, ILogger<ReviewQueue> logger)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(workers, 1);
        (_runner, _workers, _logger) = (runner, workers, logger);
    }

    /// <summary>Queues a review, collapsing it with any other request for the same pull request.</summary>
    public ReviewAdmission Enqueue(ReviewRequest request)
    {
        CancellationTokenSource? stale = null;
        ReviewAdmission admission;
        lock (_gate)
        {
            if (!_slots.TryGetValue(request.Key, out var slot)) _slots[request.Key] = slot = new PullRequestSlot();
            if (slot.Waiting is { } waiting)
            {
                // Keep an escalation alive when a plain request for the same head replaces it.
                slot.Waiting = request.Model is null && SameHead(waiting, request) ? request with { Model = waiting.Model } : request;
                _logger.LogInformation("review request for {PullRequest} at {Head} replaced a waiting request", request.Key, request.HeadSha);
                return ReviewAdmission.Superseded;
            }
            if (slot.Running is { } running)
            {
                if (SameHead(running, request) && request.Model == running.Model)
                {
                    _logger.LogInformation("review for {PullRequest} at {Head} is already running; duplicate request dropped", request.Key, request.HeadSha);
                    return ReviewAdmission.Duplicate;
                }
                if (request.HeadSha is not null && running.HeadSha is not null && !SameHead(running, request))
                {
                    _logger.LogInformation("cancelling review for {PullRequest} at {Old}: new head {New}", request.Key, running.HeadSha, request.HeadSha);
                    stale = slot.Cancellation;
                }
                // The running worker schedules the waiting request when it finishes.
                slot.Waiting = request;
                admission = ReviewAdmission.Queued;
            }
            else
            {
                slot.Waiting = request;
                _ready.Writer.TryWrite(request.Key);
                _logger.LogInformation("review for {PullRequest} at {Head} queued", request.Key, request.HeadSha);
                admission = ReviewAdmission.Queued;
            }
        }
        CancelOutsideLock(stale);
        return admission;
    }

    /// <summary>Drops waiting work and cancels any running review, e.g. when the pull request closes.</summary>
    public bool Cancel(string owner, string name, int number)
    {
        var key = new ReviewRequest(owner, name, number, null).Key;
        CancellationTokenSource? running;
        lock (_gate)
        {
            if (!_slots.TryGetValue(key, out var slot)) return false;
            // A slot without a running review is still in the ready channel;
            // the worker that dequeues it finds nothing waiting and skips it.
            slot.Waiting = null;
            running = slot.Cancellation;
        }
        _logger.LogInformation("review work for {PullRequest} cancelled", key);
        CancelOutsideLock(running);
        return true;
    }

    /// <summary>Number of pull requests with a waiting review and with a running review.</summary>
    public (int Waiting, int Running) Snapshot()
    {
        lock (_gate) return (_slots.Values.Count(x => x.Waiting is not null), _slots.Values.Count(x => x.Running is not null));
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, _workers).Select(_ => WorkAsync(stoppingToken)));

    private async Task WorkAsync(CancellationToken stoppingToken)
    {
        await foreach (var key in _ready.Reader.ReadAllAsync(stoppingToken))
        {
            ReviewRequest request;
            CancellationTokenSource cancellation;
            lock (_gate)
            {
                if (!_slots.TryGetValue(key, out var slot) || slot.Waiting is null || slot.Running is not null)
                {
                    if (slot is { Waiting: null, Running: null }) _slots.Remove(key);
                    continue;
                }
                request = slot.Waiting;
                slot.Waiting = null;
                slot.Running = request;
                slot.Cancellation = cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            }
            try { await _runner(request, cancellation.Token); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            { _logger.LogInformation("review for {PullRequest} stopped before it finished", key); }
            catch (Exception ex) { _logger.LogError(ex, "review for {PullRequest} failed", key); }
            finally
            {
                lock (_gate)
                {
                    var slot = _slots[key];
                    slot.Running = null;
                    slot.Cancellation = null;
                    if (slot.Waiting is not null) _ready.Writer.TryWrite(key);
                    else _slots.Remove(key);
                }
                cancellation.Dispose();
            }
        }
    }

    // Cancellation runs callbacks inline (killing ocr, resuming awaiters), so
    // it must never run under the gate; the worker may also have disposed the
    // source after finishing in the meantime, which leaves nothing to cancel.
    private static void CancelOutsideLock(CancellationTokenSource? cancellation)
    {
        try { cancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private static bool SameHead(ReviewRequest left, ReviewRequest right) =>
        string.Equals(left.HeadSha, right.HeadSha, StringComparison.OrdinalIgnoreCase);

    private sealed class PullRequestSlot
    {
        public ReviewRequest? Waiting { get; set; }
        public ReviewRequest? Running { get; set; }
        public CancellationTokenSource? Cancellation { get; set; }
    }
}
