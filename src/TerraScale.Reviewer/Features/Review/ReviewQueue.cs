using System.Threading.Channels;
using System.Diagnostics;
using TerraScale.Reviewer.Features.Configuration;

namespace TerraScale.Reviewer.Features.Review;

/// <summary>A request to review the current head of one pull request.</summary>
/// <param name="HeadSha">Head the request was raised for, when known. An outdated head is skipped before reviewing; the SHA also drives deduplication.</param>
/// <param name="Model">Model override (escalation), or <c>null</c> for the default model.</param>
public sealed record ReviewRequest(string Owner, string Name, int Number, string? HeadSha, string? Model = null)
{
    internal Task QueuedStatus { get; init; } = Task.CompletedTask;
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
/// <remarks>
/// Work dropped for a newer head or a closed pull request is handed to the
/// skip callback, so its head does not keep a pending status forever: waiting
/// work at once, running work after the review has stopped, so the callback
/// always has the last word on that head. Reviews stopped by a shutdown are
/// not skipped; their pending status is what startup recovery re-queues.
/// </remarks>
public sealed class ReviewQueue : BackgroundService
{
    public const string NewerHead = "a newer push replaced this head";
    public const string Closed = "the pull request was closed";

    private readonly Func<ReviewRequest, CancellationToken, Task> _runner;
    private readonly Func<ReviewRequest, string, Task> _skip;
    private readonly int _workers;
    private readonly ILogger<ReviewQueue> _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, PullRequestSlot> _slots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _publications = new(StringComparer.Ordinal);
    private readonly Channel<string> _ready = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { AllowSynchronousContinuations = false });

    public ReviewQueue(PullRequestReviewer reviewer, ReviewerOptions options, ILogger<ReviewQueue> logger)
        : this(reviewer.RunAsync, reviewer.SkipAsync, options.MaxConcurrent, logger) { }

    internal ReviewQueue(Func<ReviewRequest, CancellationToken, Task> runner, Func<ReviewRequest, string, Task> skip, int workers, ILogger<ReviewQueue> logger)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(workers, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(workers, 2);
        (_runner, _skip, _workers, _logger) = (runner, skip, workers, logger);
    }

    /// <summary>Queues a review, collapsing it with any other request for the same pull request.</summary>
    public ReviewAdmission Enqueue(ReviewRequest request, Func<ReviewRequest, Task>? publishQueued = null)
    {
        CancellationTokenSource? stale = null;
        ReviewAdmission admission;
        lock (_gate)
        {
            if (!_slots.TryGetValue(request.Key, out var slot)) _slots[request.Key] = slot = new PullRequestSlot();
            if (slot is { Running: { } running, StopReason: null })
            {
                if (SameHead(running, request) && request.Model == running.Model && slot.Waiting is null)
                {
                    _logger.LogInformation("review for {PullRequest} at {Head} is already running; duplicate request dropped", request.Key, request.HeadSha);
                    return ReviewAdmission.Duplicate;
                }
                // Also when a request is already waiting: that request
                // cancelled nothing if it was for the running head.
                if (NewHead(running, request))
                {
                    _logger.LogInformation("cancelling review for {PullRequest} at {Old}: new head {New}", request.Key, running.HeadSha, request.HeadSha);
                    stale = slot.Cancellation;
                    slot.StopReason = NewerHead;
                }
            }
            if (slot.Waiting is { } waiting)
            {
                if (NewHead(waiting, request) || request.HeadSha is null) SkipUnderLock(waiting, NewerHead);
                // Keep an escalation alive when a plain request for the same head replaces it.
                if (request.Model is null && SameHead(waiting, request)) request = request with { Model = waiting.Model };
                _logger.LogInformation("review request for {PullRequest} at {Head} replaced a waiting request", request.Key, request.HeadSha);
                admission = ReviewAdmission.Superseded;
            }
            else
            {
                slot.WaitingSince = Stopwatch.GetTimestamp();
                // A running worker schedules the waiting request when it finishes.
                if (slot.Running is null) _ready.Writer.TryWrite(request.Key);
                _logger.LogInformation("review for {PullRequest} at {Head} queued", request.Key, request.HeadSha);
                admission = ReviewAdmission.Queued;
            }
            // All queued/skip writes for a PR share a chain, even across A -> B -> A
            // and across idle slots. The next runner waits for the chain here.
            request = request with { QueuedStatus = PublishUnderLock(request.Key,
                () =>
                {
                    ReviewRequest admitted;
                    lock (_gate)
                    {
                        if (!_slots.TryGetValue(request.Key, out var current)
                            || (current.Waiting?.QueuedStatus != request.QueuedStatus
                                && current.Running?.QueuedStatus != request.QueuedStatus)) return Task.CompletedTask;
                        admitted = request;
                    }
                    return publishQueued?.Invoke(admitted) ?? Task.CompletedTask;
                }) };
            slot.Waiting = request;
            _logger.LogInformation("review queue_depth {QueueDepth}; in_flight {InFlight}",
                _slots.Values.Count(x => x.Waiting is not null), _slots.Values.Count(x => x.Running is not null));
        }
        CancelOutsideLock(stale);
        return admission;
    }

    /// <summary>Resolves an admitted command's head without losing its FIFO position.</summary>
    public ReviewRequest? ResolveHead(ReviewRequest request, string sha)
    {
        CancellationTokenSource? stale = null;
        ReviewRequest? resolved = null;
        lock (_gate)
        {
            if (!_slots.TryGetValue(request.Key, out var slot)) return null;
            var prepared = request with { HeadSha = sha };
            if (slot.Waiting?.QueuedStatus == request.QueuedStatus)
            {
                if (slot is { Running: { } running, StopReason: null }
                    && SameHead(running, prepared) && running.Model == prepared.Model)
                { slot.Waiting = null; return null; }
                slot.Waiting = prepared;
                resolved = prepared;
                if (slot is { Running: { } older, StopReason: null } && NewHead(older, prepared))
                { stale = slot.Cancellation; slot.StopReason = NewerHead; }
            }
            else if (slot.Running?.QueuedStatus == request.QueuedStatus)
            {
                slot.Running = prepared;
                if (slot.Waiting is { } newer && NewHead(prepared, newer))
                { stale = slot.Cancellation; slot.StopReason = NewerHead; }
                if (slot.StopReason is null) resolved = prepared;
            }
        }
        CancelOutsideLock(stale);
        return resolved;
    }

    /// <summary>Drops waiting work and cancels any running review when the pull request closes.</summary>
    public bool Cancel(string owner, string name, int number)
    {
        var key = new ReviewRequest(owner, name, number, null).Key;
        ReviewRequest? waiting;
        CancellationTokenSource? running = null;
        lock (_gate)
        {
            if (!_slots.TryGetValue(key, out var slot)) return false;
            // A slot without a running review is still in the ready channel;
            // the worker that dequeues it finds nothing waiting and skips it.
            waiting = slot.Waiting;
            slot.Waiting = null;
            if (waiting is not null) SkipUnderLock(waiting, Closed);
            if (slot is { Running: not null, StopReason: null })
            {
                running = slot.Cancellation;
                slot.StopReason = Closed;
            }
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
            Task publications;
            CancellationTokenSource cancellation;
            lock (_gate)
            {
                if (!_slots.TryGetValue(key, out var slot) || slot.Waiting is null || slot.Running is not null)
                {
                    if (slot is { Waiting: null, Running: null }) _slots.Remove(key);
                    continue;
                }
                request = slot.Waiting;
                // Include skips appended by the previous worker after admission.
                publications = _publications.GetValueOrDefault(key) ?? request.QueuedStatus;
                slot.Waiting = null;
                slot.Running = request;
                slot.Cancellation = cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                _logger.LogInformation("review dequeued for {PullRequest}; wait_seconds {WaitSeconds}; queue_depth {QueueDepth}; in_flight {InFlight}",
                    key, Stopwatch.GetElapsedTime(slot.WaitingSince).TotalSeconds,
                    _slots.Values.Count(x => x.Waiting is not null), _slots.Values.Count(x => x.Running is not null));
            }
            try
            {
                await publications.WaitAsync(cancellation.Token);
                lock (_gate) request = _slots[key].Running!;
                cancellation.Token.ThrowIfCancellationRequested();
                await _runner(request, cancellation.Token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            { _logger.LogInformation("review for {PullRequest} stopped before it finished", key); }
            catch (Exception ex) { _logger.LogError(ex, "review for {PullRequest} failed", key); }
            finally
            {
                string? stopReason;
                lock (_gate)
                {
                    var slot = _slots[key];
                    request = slot.Running!;
                    // A head queued again (e.g. a force-push back to it) is not skipped.
                    stopReason = slot.Waiting is { } next && SameHead(next, request) ? null : slot.StopReason;
                    slot.Running = null;
                    slot.Cancellation = null;
                    slot.StopReason = null;
                    if (stopReason is not null) SkipUnderLock(request, stopReason);
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

    // Called under the gate; the action starts asynchronously after registration.
    // Retain the chain until its final POST ends, even when the queue slot is gone.
    private Task PublishUnderLock(string key, Func<Task> action)
    {
        var previous = _publications.GetValueOrDefault(key) ?? Task.CompletedTask;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _publications[key] = completed.Task;
        _ = PublishAsync();
        return completed.Task;

        async Task PublishAsync()
        {
            await Task.Yield();
            try { await previous; await action(); }
            catch (Exception ex) { _logger.LogWarning(ex, "could not publish review status for {PullRequest}", key); }
            finally
            {
                completed.TrySetResult();
                lock (_gate)
                    if (_publications.GetValueOrDefault(key) == completed.Task) _publications.Remove(key);
            }
        }
    }

    private void SkipUnderLock(ReviewRequest request, string reason)
    {
        if (request.HeadSha is null) return;
        _ = PublishUnderLock(request.Key, async () =>
        {
            lock (_gate)
            {
                // A force-push can return to a dropped SHA before its POST runs.
                if (_slots.TryGetValue(request.Key, out var slot)
                    && ((slot.Waiting is { } waiting && SameHead(waiting, request))
                        || (slot.Running is { } running && SameHead(running, request)))) return;
            }
            _logger.LogInformation("review for {PullRequest} at {Head} skipped: {Reason}", request.Key, request.HeadSha, reason);
            await _skip(request, reason);
        });
    }

    private static bool SameHead(ReviewRequest left, ReviewRequest right) =>
        string.Equals(left.HeadSha, right.HeadSha, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="request"/> is for a different head than <paramref name="current"/>; unknown heads never count.</summary>
    private static bool NewHead(ReviewRequest current, ReviewRequest request) =>
        current.HeadSha is not null && request.HeadSha is not null && !SameHead(current, request);

    private sealed class PullRequestSlot
    {
        public ReviewRequest? Waiting { get; set; }
        public long WaitingSince { get; set; }
        public ReviewRequest? Running { get; set; }
        public CancellationTokenSource? Cancellation { get; set; }
        /// <summary>Why the running review was cancelled, when the queue cancelled it; its head is skipped once it stops.</summary>
        public string? StopReason { get; set; }
    }
}
