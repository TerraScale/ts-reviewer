using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using TerraScale.Reviewer.Features.Configuration;
using TerraScale.Reviewer.Features.Context;
using TerraScale.Reviewer.Features.GitHub;
using TerraScale.Reviewer.Features.Review;
using TerraScale.Reviewer.Features.Webhooks;
using TUnit.Assertions;
using TUnit.Core;

public sealed class ReviewQueueTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static ReviewerOptions Options(string database) =>
        new("http://+:8080", "https://github.test", "123", "https://anthropic.test", "test-key", "TerraScale", "bot", "webhook-test-secret", "context-test-token", database, 100, 5, null, "operator-test-token");

    private static ReviewRequest Request(int number, string? head) => new("TerraScale", "widgets", number, head);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DelayedQueuedStatusCannotOverwriteSuccessOrSkipped(bool replace)
    {
        var queuedPost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queuedStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminalPost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var states = new ConcurrentQueue<string>();
        var queue = new ReviewQueue((request, _) =>
        {
            if (request.HeadSha == "old") { states.Enqueue("success"); terminalPost.TrySetResult(); }
            return Task.CompletedTask;
        }, (request, _) =>
        {
            if (request.HeadSha == "old") { states.Enqueue("skipped"); terminalPost.TrySetResult(); }
            return Task.CompletedTask;
        }, 1, NullLogger<ReviewQueue>.Instance);
        await queue.StartAsync(CancellationToken.None);
        try
        {
            queue.Enqueue(Request(1, "old"), async _ =>
            {
                queuedStarted.TrySetResult();
                await queuedPost.Task;
                states.Enqueue("pending");
            });
            await queuedStarted.Task.WaitAsync(Wait);
            await WaitUntilAsync(() => queue.Snapshot() == (0, 1));
            if (replace) queue.Enqueue(Request(1, "new"));
            await Assert.That(terminalPost.Task.IsCompleted).IsFalse();
            queuedPost.TrySetResult();
            await terminalPost.Task.WaitAsync(Wait);
            await Assert.That(string.Join(",", states)).IsEqualTo(replace ? "pending,skipped" : "pending,success");
        }
        finally { queuedPost.TrySetResult(); await queue.StopAsync(CancellationToken.None); }
    }

    [Test]
    public async Task ReturningToAnEarlierShaFencesDelayedQueuedAndSkippedWrites()
    {
        var releaseOld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldPostStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var states = new ConcurrentDictionary<string, string>();
        var runs = new ConcurrentQueue<string>();
        var queue = new ReviewQueue((request, _) =>
        {
            runs.Enqueue(request.HeadSha!);
            states[request.HeadSha!] = "success";
            completed.TrySetResult();
            return Task.CompletedTask;
        }, (request, _) =>
        {
            states[request.HeadSha!] = "skipped";
            return Task.CompletedTask;
        }, 1, NullLogger<ReviewQueue>.Instance);
        queue.Enqueue(Request(1, "a"), async _ => { oldPostStarted.TrySetResult(); await releaseOld.Task; states["a"] = "pending"; });
        await oldPostStarted.Task.WaitAsync(Wait);
        queue.Enqueue(Request(1, "b"), _ => { states["b"] = "pending"; return Task.CompletedTask; });
        queue.Enqueue(Request(1, "a"), _ => { states["a"] = "pending"; return Task.CompletedTask; });
        await queue.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => queue.Snapshot() == (0, 1));
            await Assert.That(completed.Task.IsCompleted).IsFalse();
            releaseOld.TrySetResult();
            await completed.Task.WaitAsync(Wait);
            await WaitUntilAsync(() => queue.Snapshot() == (0, 0));
            await Assert.That(states["a"]).IsEqualTo("success");
            await Assert.That(states["b"]).IsEqualTo("skipped");
            await Assert.That(string.Join(",", runs)).IsEqualTo("a");
        }
        finally { releaseOld.TrySetResult(); await queue.StopAsync(CancellationToken.None); }
    }

    [Test]
    public async Task ResolvedCommandWaitsForSkipsAppendedByTheCancelledReview()
    {
        var oldStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldStopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishOld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lookupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishLookup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reviewed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var states = new ConcurrentDictionary<string, string>();
        var runs = 0;
        var skippedA = 0;
        var queue = new ReviewQueue(async (request, ct) =>
        {
            if (Interlocked.Increment(ref runs) == 1)
            {
                oldStarted.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException)
                { oldStopping.TrySetResult(); await finishOld.Task; throw; }
            }
            states[request.HeadSha!] = "success";
            reviewed.TrySetResult();
        }, (request, _) =>
        {
            if (request.HeadSha == "a") Interlocked.Increment(ref skippedA);
            states[request.HeadSha!] = "skipped";
            return Task.CompletedTask;
        }, 1, NullLogger<ReviewQueue>.Instance);
        await queue.StartAsync(CancellationToken.None);
        try
        {
            queue.Enqueue(Request(1, "a"));
            await oldStarted.Task.WaitAsync(Wait);
            queue.Enqueue(Request(1, "b"));
            await oldStopping.Task.WaitAsync(Wait);
            queue.Enqueue(Request(1, null), async admitted =>
            {
                lookupStarted.TrySetResult();
                await finishLookup.Task;
                queue.ResolveHead(admitted, "a");
                states["a"] = "pending";
            });
            await lookupStarted.Task.WaitAsync(Wait);
            finishOld.TrySetResult();
            await WaitUntilAsync(() => queue.Snapshot() == (0, 1));
            finishLookup.TrySetResult();
            await reviewed.Task.WaitAsync(Wait);
            await WaitUntilAsync(() => queue.Snapshot() == (0, 0));
            await Assert.That(states["a"]).IsEqualTo("success");
            await Assert.That(Volatile.Read(ref skippedA)).IsEqualTo(0);
            await Assert.That(Volatile.Read(ref runs)).IsEqualTo(2);
        }
        finally { finishOld.TrySetResult(); finishLookup.TrySetResult(); await queue.StopAsync(CancellationToken.None); }
    }

    [Test]
    public async Task HeadResolutionUpdatesIdentityAndDropsACommandForTheRunningHead()
    {
        var runner = new ScriptedRunner();
        var queue = await StartAsync(runner, 1);
        try
        {
            queue.Enqueue(Request(1, "a"));
            await runner.Started(1, "a").WaitAsync(Wait);
            var resolved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            queue.Enqueue(Request(1, null), admitted =>
            {
                var duplicate = queue.ResolveHead(admitted, "a");
                resolved.TrySetResult();
                return duplicate is null ? Task.CompletedTask : Task.FromException(new InvalidOperationException("duplicate survived resolution"));
            });
            await resolved.Task.WaitAsync(Wait);
            await Assert.That(queue.Snapshot()).IsEqualTo((0, 1));
            runner.Release(1, "a");
            await WaitUntilAsync(() => queue.Snapshot() == (0, 0));
            await Assert.That(runner.Runs.Count).IsEqualTo(1);

            queue.Enqueue(Request(2, null), admitted =>
            {
                queue.ResolveHead(admitted, "b");
                return Task.CompletedTask;
            });
            await runner.Started(2, "b").WaitAsync(Wait);
            await Assert.That(runner.Runs.Last().HeadSha).IsEqualTo("b");
            runner.Release(2, "b");
        }
        finally { await queue.StopAsync(CancellationToken.None); }
    }

    [Test]
    public async Task ReplacementKeepsItsFifoPositionAcrossDifferentPullRequests()
    {
        var runner = new ScriptedRunner();
        var queue = new ReviewQueue(runner.RunAsync, runner.SkipAsync, 1, NullLogger<ReviewQueue>.Instance);
        queue.Enqueue(Request(1, "a"));
        queue.Enqueue(Request(2, "old"));
        queue.Enqueue(Request(3, "c"));
        queue.Enqueue(Request(2, "new"));
        await queue.StartAsync(CancellationToken.None);
        try
        {
            foreach (var (number, head) in new[] { (1, "a"), (2, "new"), (3, "c") })
            {
                await runner.Started(number, head).WaitAsync(Wait);
                runner.Release(number, head);
            }
            await WaitUntilAsync(() => queue.Snapshot() == (0, 0));
            await Assert.That(string.Join(",", runner.Runs.Select(x => x.Number))).IsEqualTo("1,2,3");
        }
        finally { await queue.StopAsync(CancellationToken.None); }
    }

    [Test]
    public async Task CommentWebhookKeepsFifoAdmissionWhileResolvingAndMarkingItsHeadPending()
    {
        var database = Path.Combine(Path.GetTempPath(), "ts-reviewer-" + Guid.NewGuid().ToString("N") + ".db");
        var options = Options(database);
        var transport = new CommentHeadHandler();
        var github = new GitHubClient(new HttpClient(transport), options, (_, _, _) => Task.FromResult("fixture-token"));
        var runner = new ScriptedRunner();
        var queue = new ReviewQueue(runner.RunAsync, runner.SkipAsync, 1, NullLogger<ReviewQueue>.Instance);
        using var store = new ContextStore(options);
        var indexer = new ContextIndexer(github, store, new ContextEmbedder(), options, NullLogger<ContextIndexer>.Instance);
        var reviewer = new PullRequestReviewer(github, new ReviewEngine(options, NullLogger<ReviewEngine>.Instance), options, NullLogger<PullRequestReviewer>.Instance);
        var webhook = new WebhookHandler(github, queue, reviewer, indexer, options, NullLogger<WebhookHandler>.Instance);
        try
        {
            var response = await PostAsync(webhook, new
            {
                action = "created",
                repository = new { name = "widgets", owner = new { login = "TerraScale" } },
                issue = new { number = 7, pull_request = new { } },
                comment = new { id = 1, body = "@bot recheck", user = new { login = "author" } },
            }).WaitAsync(TimeSpan.FromSeconds(1));
            await Assert.That(((IStatusCodeHttpResult)response).StatusCode).IsEqualTo(StatusCodes.Status202Accepted);
            await Assert.That(queue.Snapshot()).IsEqualTo((1, 0));
            transport.ReleaseHead.TrySetResult();
            await Assert.That(await transport.PendingHead.Task.WaitAsync(Wait)).IsEqualTo("command-head");
            await queue.StartAsync(CancellationToken.None);
            await runner.Started(7, "command-head").WaitAsync(Wait);
            runner.Release(7, "command-head");
        }
        finally { transport.ReleaseHead.TrySetResult(); await queue.StopAsync(CancellationToken.None); File.Delete(database); }
    }

    [Test]
    public async Task AQueuedOutdatedHeadIsSkippedBeforeStartingTheCurrentHead()
    {
        var options = Options("unused.db");
        var transport = new MovedHeadHandler();
        var github = new GitHubClient(new HttpClient(transport), options, (_, _, _) => Task.FromResult("fixture-token"));
        var reviewer = new PullRequestReviewer(github, new ReviewEngine(options, NullLogger<ReviewEngine>.Instance), options, NullLogger<PullRequestReviewer>.Instance);
        var oldHead = new string('a', 40);
        await reviewer.RunAsync(Request(7, oldHead), CancellationToken.None);
        await Assert.That(transport.Requests.Count).IsEqualTo(3);
        await Assert.That(transport.Requests.Last()).IsEqualTo("/repos/TerraScale/widgets/statuses/" + oldHead);
        await Assert.That(transport.State).IsEqualTo("error");
    }

    [Test]
    public async Task WebhookEnqueuesAfterAuthorizationWithoutWaitingForHeadOrStatus()
    {
        var database = Path.Combine(Path.GetTempPath(), "ts-reviewer-" + Guid.NewGuid().ToString("N") + ".db");
        var options = Options(database);
        var blocked = new BlockedHandler();
        var github = new GitHubClient(new HttpClient(blocked), options, (_, _, _) => Task.FromResult("fixture-token"));
        var queue = new ReviewQueue((_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask, 2, NullLogger<ReviewQueue>.Instance);
        using var store = new ContextStore(options);
        var indexer = new ContextIndexer(github, store, new ContextEmbedder(), options, NullLogger<ContextIndexer>.Instance);
        var reviewer = new PullRequestReviewer(github, new ReviewEngine(options, NullLogger<ReviewEngine>.Instance), options, NullLogger<PullRequestReviewer>.Instance);
        var webhook = new WebhookHandler(github, queue, reviewer, indexer, options, NullLogger<WebhookHandler>.Instance);
        try
        {
            var response = await PostAsync(webhook, new
            {
                action = "created",
                repository = new { name = "widgets", owner = new { login = "TerraScale" } },
                issue = new { number = 7, pull_request = new { } },
                comment = new { id = 1, body = "@bot recheck", user = new { login = "author" } },
            }).WaitAsync(TimeSpan.FromSeconds(1));
            await Assert.That(((IStatusCodeHttpResult)response).StatusCode).IsEqualTo(StatusCodes.Status202Accepted);
            await Assert.That(queue.Snapshot()).IsEqualTo((1, 0));
        }
        finally { blocked.Release(); File.Delete(database); }
    }

    [Test]
    public async Task RequestsForAWaitingPullRequestCollapseIntoOneReviewOfTheNewestHead()
    {
        var runner = new ScriptedRunner();
        var queue = await StartAsync(runner, workers: 1);
        try
        {
            // PR 1 occupies the only worker so PR 2's requests stay waiting.
            await Assert.That(queue.Enqueue(Request(1, "a"))).IsEqualTo(ReviewAdmission.Queued);
            await runner.Started(1, "a").WaitAsync(Wait);
            await Assert.That(queue.Enqueue(Request(2, "old"))).IsEqualTo(ReviewAdmission.Queued);
            await Assert.That(queue.Enqueue(Request(2, "old"))).IsEqualTo(ReviewAdmission.Superseded);
            await Assert.That(queue.Enqueue(Request(2, "new"))).IsEqualTo(ReviewAdmission.Superseded);
            await Assert.That(queue.Snapshot()).IsEqualTo((1, 1));

            runner.Release(1, "a");
            await runner.Started(2, "new").WaitAsync(Wait);
            runner.Release(2, "new");
            await WaitUntilAsync(() => queue.Snapshot() == (0, 0));
            await Assert.That(runner.Runs.Count(x => x.Number == 2)).IsEqualTo(1);
        }
        finally { await queue.StopAsync(CancellationToken.None); }
    }

    [Test]
    public async Task RequestForTheHeadAlreadyUnderReviewIsDropped()
    {
        var runner = new ScriptedRunner();
        var queue = await StartAsync(runner, workers: 1);
        try
        {
            queue.Enqueue(Request(1, "a"));
            await runner.Started(1, "a").WaitAsync(Wait);
            await Assert.That(queue.Enqueue(Request(1, "A"))).IsEqualTo(ReviewAdmission.Duplicate);
            runner.Release(1, "a");
            await WaitUntilAsync(() => queue.Snapshot() == (0, 0));
            await Assert.That(runner.Runs.Count).IsEqualTo(1);
        }
        finally { await queue.StopAsync(CancellationToken.None); }
    }

    [Test]
    public async Task NewHeadCancelsTheRunningReviewAndReviewsTheNewHeadNext()
    {
        var runner = new ScriptedRunner();
        var queue = await StartAsync(runner, workers: 1);
        try
        {
            queue.Enqueue(Request(1, "old"));
            await runner.Started(1, "old").WaitAsync(Wait);
            await Assert.That(queue.Enqueue(Request(1, "new"))).IsEqualTo(ReviewAdmission.Queued);
            await Assert.That(await runner.Cancelled(1, "old").WaitAsync(Wait)).IsTrue();
            await runner.Started(1, "new").WaitAsync(Wait);
            runner.Release(1, "new");
            await WaitUntilAsync(() => queue.Snapshot() == (0, 0));
            await Assert.That(string.Join(",", runner.Runs.Select(x => x.HeadSha))).IsEqualTo("old,new");
        }
        finally { await queue.StopAsync(CancellationToken.None); }
    }

    [Test]
    public async Task CancelDropsWaitingWorkAndStopsTheRunningReview()
    {
        var runner = new ScriptedRunner();
        var queue = await StartAsync(runner, workers: 1);
        try
        {
            queue.Enqueue(Request(1, "a"));
            await runner.Started(1, "a").WaitAsync(Wait);
            queue.Enqueue(Request(2, "b"));
            await Assert.That(queue.Cancel("TerraScale", "widgets", 2)).IsTrue();
            await Assert.That(queue.Cancel("terrascale", "WIDGETS", 1)).IsTrue();
            await Assert.That(await runner.Cancelled(1, "a").WaitAsync(Wait)).IsTrue();
            await WaitUntilAsync(() => queue.Snapshot() == (0, 0));
            await Assert.That(queue.Cancel("TerraScale", "widgets", 3)).IsFalse();

            // The queue keeps working after cancelled and dropped entries.
            queue.Enqueue(Request(2, "c"));
            await runner.Started(2, "c").WaitAsync(Wait);
            runner.Release(2, "c");
            await Assert.That(runner.Runs.Any(x => x is { Number: 2, HeadSha: "b" })).IsFalse();
        }
        finally { await queue.StopAsync(CancellationToken.None); }
    }

    [Test]
    public async Task NewHeadCancelsTheRunningReviewEvenWhenARequestIsWaiting()
    {
        var runner = new ScriptedRunner();
        var queue = await StartAsync(runner, workers: 1);
        try
        {
            // An unresolved command waits until its head is resolved.
            queue.Enqueue(Request(1, "old"));
            await runner.Started(1, "old").WaitAsync(Wait);
            await Assert.That(queue.Enqueue(Request(1, null))).IsEqualTo(ReviewAdmission.Queued);
            await Assert.That(queue.Enqueue(Request(1, "new"))).IsEqualTo(ReviewAdmission.Superseded);
            await Assert.That(await runner.Cancelled(1, "old").WaitAsync(Wait)).IsTrue();
            await runner.Started(1, "new").WaitAsync(Wait);
            runner.Release(1, "new");
            await WaitUntilAsync(() => queue.Snapshot() == (0, 0));
            await Assert.That(string.Join(",", runner.Runs.Select(x => x.HeadSha))).IsEqualTo("old,new");
        }
        finally { await queue.StopAsync(CancellationToken.None); }
    }

    [Test]
    public async Task DroppedWorkMarksItsHeadSkippedButAShutdownLeavesItPending()
    {
        var runner = new ScriptedRunner();
        var queue = await StartAsync(runner, workers: 1);
        try
        {
            // A newer push replaces waiting work at once...
            queue.Enqueue(Request(1, "a"));
            await runner.Started(1, "a").WaitAsync(Wait);
            queue.Enqueue(Request(2, "old"));
            queue.Enqueue(Request(2, "new"));
            await WaitUntilAsync(() => runner.Skipped(2, "old") == ReviewQueue.NewerHead);
            // ...and running work only once it has stopped.
            queue.Enqueue(Request(1, "b"));
            await Assert.That(await runner.Cancelled(1, "a").WaitAsync(Wait)).IsTrue();
            await WaitUntilAsync(() => runner.Skipped(1, "a") == ReviewQueue.NewerHead);

            // Closing drops the waiting and the running review alike.
            await runner.Started(2, "new").WaitAsync(Wait);
            queue.Cancel("TerraScale", "widgets", 1);
            queue.Cancel("TerraScale", "widgets", 2);
            await WaitUntilAsync(() => runner.Skipped(1, "b") == ReviewQueue.Closed && runner.Skipped(2, "new") == ReviewQueue.Closed);

            // Re-requesting the same head skips nothing.
            queue.Enqueue(Request(3, "c"));
            await runner.Started(3, "c").WaitAsync(Wait);
            await Assert.That(queue.Enqueue(Request(3, "c"))).IsEqualTo(ReviewAdmission.Duplicate);
            await Assert.That(runner.Skipped(3, "c")).IsNull();
        }
        finally { await queue.StopAsync(CancellationToken.None); }
        // A shutdown stops the running review without skipping it.
        await Assert.That(await runner.Cancelled(3, "c").WaitAsync(Wait)).IsTrue();
        await Assert.That(runner.Skipped(3, "c")).IsNull();
    }

    [Test]
    public async Task WorkersStayWithinTheLimitAndSurviveFailingReviews()
    {
        var running = 0;
        var peak = 0;
        var completed = 0;
        var queue = new ReviewQueue(async (request, ct) =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            try
            {
                await Task.Delay(50, ct);
                if (request.Number % 2 == 0) throw new InvalidOperationException("review exploded");
            }
            finally
            {
                Interlocked.Decrement(ref running);
                Interlocked.Increment(ref completed);
            }
        }, (_, _) => Task.CompletedTask, workers: ReviewQueue.WorkerCount, NullLogger<ReviewQueue>.Instance);
        await queue.StartAsync(CancellationToken.None);
        try
        {
            for (var number = 1; number <= 8; number++) queue.Enqueue(Request(number, "h" + number));
            await WaitUntilAsync(() => Volatile.Read(ref completed) == 8);
            await Assert.That(Volatile.Read(ref peak)).IsEqualTo(2);

            // Every failure released its worker: a new request still runs.
            queue.Enqueue(Request(9, "h9"));
            await WaitUntilAsync(() => Volatile.Read(ref completed) == 9);
            await WaitUntilAsync(() => queue.Snapshot() == (0, 0));
        }
        finally { await queue.StopAsync(CancellationToken.None); }
    }

    [Test]
    public async Task WebhookIgnoresTheBotsOwnCommentsAndCancelsClosedPullRequests()
    {
        var database = Path.Combine(Path.GetTempPath(), "ts-reviewer-" + Guid.NewGuid().ToString("N") + ".db");
        var options = Options(database);
        var transport = new StatusOnlyHandler();
        var github = new GitHubClient(new HttpClient(transport), options, (_, _, _) => Task.FromResult("fixture-token"));
        var runner = new ScriptedRunner();
        var queue = await StartAsync(runner, workers: 1);
        using var store = new ContextStore(options);
        var indexer = new ContextIndexer(github, store, new ContextEmbedder(), options, NullLogger<ContextIndexer>.Instance);
        var reviewer = new PullRequestReviewer(github, new ReviewEngine(options, NullLogger<ReviewEngine>.Instance), options, NullLogger<PullRequestReviewer>.Instance);
        var webhook = new WebhookHandler(github, queue, reviewer, indexer, options, NullLogger<WebhookHandler>.Instance);
        try
        {
            var own = await PostAsync(webhook, new
            {
                action = "created",
                repository = new { name = "widgets", owner = new { login = "TerraScale" } },
                issue = new { number = 5, pull_request = new { } },
                comment = new { id = 1, body = "Findings quote `@bot recheck` and\n@bot recheck", user = new { login = "Bot" } },
                sender = new { login = "Bot" },
            });
            await Assert.That(((IStatusCodeHttpResult)own).StatusCode).IsEqualTo(StatusCodes.Status204NoContent);

            await PostAsync(webhook, new
            {
                action = "created",
                repository = new { name = "widgets", owner = new { login = "TerraScale" } },
                issue = new { number = 5, pull_request = new { } },
                comment = new { id = 2, body = "@bot recheck", user = new { login = "author" } },
                sender = new { login = "author" },
            });
            await runner.Started(5, "a").WaitAsync(Wait);
            await Assert.That(runner.Runs.Count).IsEqualTo(1);

            transport.PullState = "closed";
            var closed = await PostAsync(webhook, new
            {
                action = "closed",
                number = 5,
                repository = new { name = "widgets", owner = new { login = "TerraScale" } },
                pull_request = new { number = 5, head = new { sha = "a" } },
            });
            await Assert.That(((IStatusCodeHttpResult)closed).StatusCode).IsEqualTo(StatusCodes.Status202Accepted);
            await Assert.That(await runner.Cancelled(5, "a").WaitAsync(Wait)).IsTrue();
            await WaitUntilAsync(() => runner.Skipped(5, "a") == ReviewQueue.Closed);
        }
        finally { await queue.StopAsync(CancellationToken.None); File.Delete(database); }
    }

    [Test]
    public async Task StartupRecoveryQueuesOpenPullRequestsLeftPendingOrNeverReviewed()
    {
        var options = Options("unused.db");
        var handler = new RecoveryHandler();
        var github = new GitHubClient(new HttpClient(handler), options, (_, _, _) => Task.FromResult("fixture-token"));
        var runner = new ScriptedRunner();
        var queue = new ReviewQueue(runner.RunAsync, runner.SkipAsync, workers: 1, NullLogger<ReviewQueue>.Instance);
        var reviewer = new PullRequestReviewer(github, new ReviewEngine(options, NullLogger<ReviewEngine>.Instance), options, NullLogger<PullRequestReviewer>.Instance);
        var recovery = new ReviewRecovery(github, queue, reviewer, options, NullLogger<ReviewRecovery>.Instance);

        await Assert.That(await recovery.RecoverAsync(CancellationToken.None)).IsEqualTo(2);
        await Assert.That(queue.Snapshot()).IsEqualTo((2, 0));
        // Admission must not overwrite a trusted terminal status before the worker deduplicates it.
        await Assert.That(handler.QueuedHeads.Count).IsEqualTo(0);
        await queue.StartAsync(CancellationToken.None);
        try
        {
            await runner.Started(7, "pending-head").WaitAsync(Wait);
            runner.Release(7, "pending-head");
            await runner.Started(9, "unreviewed-head").WaitAsync(Wait);
            runner.Release(9, "unreviewed-head");
            // Finished reviews are not re-run, whatever their outcome.
            await Assert.That(runner.Runs.Any(x => x.Number is 8 or 10 or 11)).IsFalse();
        }
        finally { await queue.StopAsync(CancellationToken.None); }
    }

    private static async Task<ReviewQueue> StartAsync(ScriptedRunner runner, int workers)
    {
        var queue = new ReviewQueue(runner.RunAsync, runner.SkipAsync, workers, NullLogger<ReviewQueue>.Instance);
        await queue.StartAsync(CancellationToken.None);
        return queue;
    }

    [Test]
    public async Task DelayedClosureLookupCannotCancelANewerAdmission()
    {
        var runner = new ScriptedRunner();
        var queue = await StartAsync(runner, 1);
        var closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            queue.Enqueue(Request(7, "old"));
            await runner.Started(7, "old").WaitAsync(Wait);
            var verification = queue.CancelWhenClosedAsync("TerraScale", "widgets", 7, () => closed.Task);
            queue.Enqueue(Request(7, "current"));
            await runner.Started(7, "current").WaitAsync(Wait);
            closed.TrySetResult(true);
            await verification;
            await Assert.That(queue.Snapshot()).IsEqualTo((0, 1));
            runner.Release(7, "current");
        }
        finally { closed.TrySetResult(false); await queue.StopAsync(CancellationToken.None); }
    }

    private static async Task<IResult> PostAsync(WebhookHandler handler, object payload)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.Headers["X-Hub-Signature-256"] = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("webhook-test-secret"), bytes));
        context.Request.Headers["X-GitHub-Event"] = JsonSerializer.SerializeToElement(payload).TryGetProperty("comment", out _) ? "issue_comment" : "pull_request";
        context.Request.Headers["X-GitHub-Delivery"] = Guid.NewGuid().ToString("D");
        return await handler.HandleAsync(context.Request, CancellationToken.None);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Wait);
        while (!condition() && !timeout.Token.IsCancellationRequested) await Task.Delay(10);
        await Assert.That(condition()).IsTrue();
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current) { }
    }

    /// <summary>A review runner whose runs block until the test releases or the queue cancels them.</summary>
    private sealed class ScriptedRunner
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _started = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _released = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _cancelled = new();
        private readonly ConcurrentDictionary<string, string> _skipped = new();
        public ConcurrentQueue<ReviewRequest> Runs { get; } = new();

        public string? Skipped(int number, string head) => _skipped.GetValueOrDefault(Key(number, head));

        public Task SkipAsync(ReviewRequest request, string reason)
        {
            _skipped[Key(request.Number, request.HeadSha!)] = reason;
            return Task.CompletedTask;
        }

        public Task Started(int number, string head) => Signal(_started, number, head).Task;
        public Task<bool> Cancelled(int number, string head) => _cancelled.GetOrAdd(Key(number, head), _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        public void Release(int number, string head) => Signal(_released, number, head).TrySetResult();

        public async Task RunAsync(ReviewRequest request, CancellationToken ct)
        {
            Runs.Enqueue(request);
            Signal(_started, request.Number, request.HeadSha!).TrySetResult();
            try { await Signal(_released, request.Number, request.HeadSha!).Task.WaitAsync(ct); }
            catch (OperationCanceledException)
            {
                _cancelled.GetOrAdd(Key(request.Number, request.HeadSha!), _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult(true);
                throw;
            }
        }

        private static TaskCompletionSource Signal(ConcurrentDictionary<string, TaskCompletionSource> map, int number, string head) =>
            map.GetOrAdd(Key(number, head), _ => new(TaskCreationOptions.RunContinuationsAsynchronously));

        private static string Key(int number, string head) => number + "@" + head.ToLowerInvariant();
    }

    private sealed class CommentHeadHandler : HttpMessageHandler
    {
        public TaskCompletionSource ReleaseHead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string> PendingHead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.Contains("/collaborators/", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { permission = "write" }) };
            if (request.Method == HttpMethod.Get)
            {
                await ReleaseHead.Task.WaitAsync(cancellationToken);
                var repository = new Repository(new Owner("TerraScale"), "widgets");
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(
                    new PullRequest(7, "change", "", new Branch("base", "main", repository), new Branch("command-head", "feature", repository)))
                };
            }
            if (request.RequestUri!.AbsolutePath.Contains("/statuses/", StringComparison.Ordinal))
            {
                var status = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
                if (status.GetProperty("state").GetString() == "pending") PendingHead.TrySetResult(request.RequestUri.AbsolutePath.Split('/')[^1]);
            }
            return new HttpResponseMessage(HttpStatusCode.Created) { Content = JsonContent.Create(new { }) };
        }
    }

    private sealed class MovedHeadHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public string? State { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath.EndsWith("/statuses", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Array.Empty<ContextStatus>()) };
            if (request.Method == HttpMethod.Get)
            {
                var repository = new Repository(new Owner("TerraScale"), "widgets");
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(
                    new PullRequest(7, "change", "", new Branch(new string('c', 40), "main", repository), new Branch(new string('b', 40), "feature", repository)))
                };
            }
            var status = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
            State = status.GetProperty("state").GetString();
            return new HttpResponseMessage(HttpStatusCode.Created) { Content = JsonContent.Create(new { }) };
        }
    }

    private sealed class BlockedHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => _release.TrySetResult();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.Contains("/collaborators/", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { permission = "write" }) };
            await _release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { }) };
        }
    }

    private sealed class StatusOnlyHandler : HttpMessageHandler
    {
        public string PullState { get; set; } = "open";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.Contains("/collaborators/", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { permission = "write" }) });
            if (request.Method == HttpMethod.Get)
            {
                var repository = new Repository(new Owner("TerraScale"), "widgets");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(
                    new PullRequest(5, "change", "", new Branch("base", "main", repository), new Branch("a", "feature", repository), PullState)) });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { }) });
        }
    }

    private sealed class RecoveryHandler : HttpMessageHandler
    {
        public ConcurrentBag<string> QueuedHeads { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path.StartsWith("/repos/TerraScale/widgets/statuses/", StringComparison.Ordinal))
            {
                var status = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
                if (status.GetProperty("state").GetString() == "pending") QueuedHeads.Add(path[(path.LastIndexOf('/') + 1)..]);
                return new HttpResponseMessage(HttpStatusCode.Created) { Content = JsonContent.Create(new { }) };
            }
            object body = path switch
            {
                "/installation/repositories" when HttpUtility.ParseQueryString(request.RequestUri.Query)["page"] == "1" => new
                {
                    repositories = new object[]
                    {
                        new { name = "widgets", owner = new { login = "TerraScale" }, archived = false },
                        new { name = "retired", owner = new { login = "TerraScale" }, archived = true },
                        new { name = "elsewhere", owner = new { login = "someone-else" }, archived = false },
                    },
                },
                "/repos/TerraScale/widgets/pulls" => new object[] { Pull(7, "pending-head"), Pull(8, "reviewed-head"), Pull(9, "unreviewed-head"), Pull(10, "errored-head"), Pull(11, "rejected-head") },
                "/repos/TerraScale/widgets/commits/pending-head/statuses" => Status(7, ("ci", "success"), ("ts-reviewer", "pending")),
                "/repos/TerraScale/widgets/commits/reviewed-head/statuses" => Status(8, ("ci", "pending"), ("ts-reviewer", "success")),
                "/repos/TerraScale/widgets/commits/unreviewed-head/statuses" => Status(9, ("ci", "pending")),
                "/repos/TerraScale/widgets/commits/errored-head/statuses" => Status(10, ("ts-reviewer", "error")),
                "/repos/TerraScale/widgets/commits/rejected-head/statuses" => Status(11, ("ts-reviewer", "failure")),
                _ => throw new InvalidOperationException("unexpected request " + request.RequestUri.PathAndQuery),
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
        }

        private static object Pull(int number, string head)
        {
            var repo = new { name = "widgets", owner = new { login = "TerraScale" } };
            return new { number, title = "t", body = "", @base = new { sha = "base", @ref = "main", repo }, head = new { sha = head, @ref = "feature", repo } };
        }

        private static object Status(int number, params (string Context, string State)[] statuses) =>
            statuses.Select(x => new { context = x.Context, state = x.State,
                description = PullRequestReviewer.StatusDescription(number, "base", "Review complete"), creator = new { login = "bot" } }).ToArray();
    }
}
