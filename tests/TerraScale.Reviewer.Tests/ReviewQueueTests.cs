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
using TerraScale.Reviewer.Features.Forgejo;
using TerraScale.Reviewer.Features.Review;
using TerraScale.Reviewer.Features.Webhooks;
using TUnit.Assertions;
using TUnit.Core;

public sealed class ReviewQueueTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static ReviewerOptions Options(string database) =>
        new("http://+:8080", "https://forgejo.test", "forgejo", "https://proxy.test/v1", "proxy", "review", "TerraScale", "bot", 1,
            TimeSpan.FromMinutes(1), "webhook-test-secret", "context-test-token", database, 100, 5, null, "operator-test-token");

    private static ReviewRequest Request(int number, string? head, string? model = null) => new("TerraScale", "widgets", number, head, model);

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
    public async Task EscalationSurvivesAPlainRequestForTheSameHead()
    {
        var runner = new ScriptedRunner();
        var queue = await StartAsync(runner, workers: 1);
        try
        {
            queue.Enqueue(Request(1, "a"));
            await runner.Started(1, "a").WaitAsync(Wait);
            queue.Enqueue(Request(2, "b", "strong-model"));
            await Assert.That(queue.Enqueue(Request(2, "b"))).IsEqualTo(ReviewAdmission.Superseded);
            runner.Release(1, "a");
            await runner.Started(2, "b").WaitAsync(Wait);
            await Assert.That(runner.Runs.Single(x => x.Number == 2).Model).IsEqualTo("strong-model");
            runner.Release(2, "b");
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
        }, workers: 2, NullLogger<ReviewQueue>.Instance);
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
        var forgejo = new ForgejoClient(new HttpClient(new StatusOnlyHandler()), options);
        var runner = new ScriptedRunner();
        var queue = await StartAsync(runner, workers: 1);
        using var store = new ContextStore(options);
        var indexer = new ContextIndexer(forgejo, store, new ContextEmbedder(), options, NullLogger<ContextIndexer>.Instance);
        var reviewer = new PullRequestReviewer(forgejo, new ReviewEngine(options, NullLogger<ReviewEngine>.Instance), options, NullLogger<PullRequestReviewer>.Instance);
        var webhook = new WebhookHandler(forgejo, queue, reviewer, indexer, options, NullLogger<WebhookHandler>.Instance);
        try
        {
            var own = await PostAsync(webhook, new
            {
                action = "created",
                repository = new { name = "widgets", owner = new { login = "TerraScale" } },
                pull_request = new { number = 5, head = new { sha = "a" } },
                comment = new { id = 1, body = "Findings quote `@bot recheck` and\n@bot recheck", user = new { login = "Bot" } },
                sender = new { login = "Bot" },
            });
            await Assert.That(((IStatusCodeHttpResult)own).StatusCode).IsEqualTo(StatusCodes.Status204NoContent);

            await PostAsync(webhook, new
            {
                action = "created",
                repository = new { name = "widgets", owner = new { login = "TerraScale" } },
                pull_request = new { number = 5, head = new { sha = "a" } },
                comment = new { id = 2, body = "@bot recheck", user = new { login = "author" } },
                sender = new { login = "author" },
            });
            await runner.Started(5, "a").WaitAsync(Wait);
            await Assert.That(runner.Runs.Count).IsEqualTo(1);

            var closed = await PostAsync(webhook, new
            {
                action = "closed",
                number = 5,
                repository = new { name = "widgets", owner = new { login = "TerraScale" } },
                pull_request = new { number = 5, head = new { sha = "a" } },
            });
            await Assert.That(((IStatusCodeHttpResult)closed).StatusCode).IsEqualTo(StatusCodes.Status202Accepted);
            await Assert.That(await runner.Cancelled(5, "a").WaitAsync(Wait)).IsTrue();
        }
        finally { await queue.StopAsync(CancellationToken.None); File.Delete(database); }
    }

    [Test]
    public async Task StartupRecoveryQueuesOpenPullRequestsLeftPendingOrNeverReviewed()
    {
        var options = Options("unused.db");
        var forgejo = new ForgejoClient(new HttpClient(new RecoveryHandler()), options);
        var runner = new ScriptedRunner();
        var queue = new ReviewQueue(runner.RunAsync, workers: 1, NullLogger<ReviewQueue>.Instance);
        var recovery = new ReviewRecovery(forgejo, queue, options, NullLogger<ReviewRecovery>.Instance);

        await Assert.That(await recovery.RecoverAsync(CancellationToken.None)).IsEqualTo(2);
        await Assert.That(queue.Snapshot()).IsEqualTo((2, 0));
        await queue.StartAsync(CancellationToken.None);
        try
        {
            await runner.Started(7, "pending-head").WaitAsync(Wait);
            runner.Release(7, "pending-head");
            await runner.Started(9, "unreviewed-head").WaitAsync(Wait);
            runner.Release(9, "unreviewed-head");
            await Assert.That(runner.Runs.Any(x => x.Number == 8)).IsFalse();
        }
        finally { await queue.StopAsync(CancellationToken.None); }
    }

    private static async Task<ReviewQueue> StartAsync(ScriptedRunner runner, int workers)
    {
        var queue = new ReviewQueue(runner.RunAsync, workers, NullLogger<ReviewQueue>.Instance);
        await queue.StartAsync(CancellationToken.None);
        return queue;
    }

    private static async Task<IResult> PostAsync(WebhookHandler handler, object payload)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.Headers["X-Forgejo-Signature"] = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("webhook-test-secret"), bytes));
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
        public ConcurrentQueue<ReviewRequest> Runs { get; } = new();

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

    private sealed class StatusOnlyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { }) });
    }

    private sealed class RecoveryHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            object body = path switch
            {
                "/api/v1/repos/search" when HttpUtility.ParseQueryString(request.RequestUri.Query)["page"] == "1" => new
                {
                    ok = true,
                    data = new object[]
                    {
                        new { name = "widgets", owner = new { login = "TerraScale" }, archived = false },
                        new { name = "retired", owner = new { login = "TerraScale" }, archived = true },
                        new { name = "elsewhere", owner = new { login = "someone-else" }, archived = false },
                    },
                },
                "/api/v1/repos/TerraScale/widgets/pulls" => new object[] { Pull(7, "pending-head"), Pull(8, "reviewed-head"), Pull(9, "unreviewed-head") },
                "/api/v1/repos/TerraScale/widgets/commits/pending-head/status" => Status(("ci", "success"), ("ts-reviewer", "pending")),
                "/api/v1/repos/TerraScale/widgets/commits/reviewed-head/status" => Status(("ci", "pending"), ("ts-reviewer", "success")),
                "/api/v1/repos/TerraScale/widgets/commits/unreviewed-head/status" => Status(("ci", "pending")),
                _ => throw new InvalidOperationException("unexpected request " + request.RequestUri.PathAndQuery),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) });
        }

        private static object Pull(int number, string head)
        {
            var repo = new { name = "widgets", owner = new { login = "TerraScale" } };
            return new { number, title = "t", body = "", @base = new { sha = "base", @ref = "main", repo }, head = new { sha = head, @ref = "feature", repo } };
        }

        private static object Status(params (string Context, string State)[] statuses) =>
            new { statuses = statuses.Select(x => new { context = x.Context, status = x.State, description = "" }) };
    }
}
