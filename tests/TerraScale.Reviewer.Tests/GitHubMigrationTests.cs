using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using TerraScale.Reviewer.Features.Configuration;
using TerraScale.Reviewer.Features.Context;
using TerraScale.Reviewer.Features.GitHub;
using TerraScale.Reviewer.Features.Review;
using TerraScale.Reviewer.Features.Webhooks;
using TUnit.Assertions;
using TUnit.Core;

public sealed class GitHubMigrationTests
{
    private static readonly ContextEmbedder Embedder = new();
    private static ReviewerOptions Options(string database = "unused") => new("http://+:8080", "https://github.test", "123",
        "https://anthropic.test", "test-key", "TerraScale", "reviewer[bot]", "webhook-test-secret", "context-test-token", database,
        100, 5, null, "operator-test-token", "456", "unused");

    [Test]
    public async Task SignatureRequiresGitHubSha256PrefixAndExactDigest()
    {
        var digest = Convert.ToHexString(HMACSHA256.HashData("webhook-test-secret"u8.ToArray(), "{}"u8));
        await Assert.That(WebhookHandler.ValidSignature("{}"u8, "sha256=" + digest, "webhook-test-secret")).IsTrue();
        await Assert.That(WebhookHandler.ValidSignature("{}"u8, digest, "webhook-test-secret")).IsFalse();
        await Assert.That(WebhookHandler.ValidSignature("{}"u8, "sha1=" + digest, "webhook-test-secret")).IsFalse();
        await Assert.That(WebhookHandler.ValidSignature("[]"u8, "sha256=" + digest, "webhook-test-secret")).IsFalse();
    }

    [Test]
    public async Task ClientUsesGitHubRoutesBearerAuthAndRightSideLineComments()
    {
        var transport = new Transport();
        var github = Client(transport);
        await github.ReviewAsync("TerraScale", "widgets", 7,
            new PullReview("findings", "REQUEST_CHANGES", new string('a', 40), [new ReviewComment("fix", "src/File.cs", 12)]), CancellationToken.None);
        await github.StatusAsync("TerraScale", "widgets", new string('a', 40),
            new CommitStatus("failure", null, new string('d', 150), "ts-reviewer"), CancellationToken.None);
        var review = transport.Requests.ToArray()[0];
        using var body = JsonDocument.Parse(review.Body!);
        var comment = body.RootElement.GetProperty("comments")[0];
        await Assert.That(review.Path).IsEqualTo("/repos/TerraScale/widgets/pulls/7/reviews");
        await Assert.That(review.Authenticated).IsTrue();
        await Assert.That(comment.GetProperty("line").GetInt32()).IsEqualTo(12);
        await Assert.That(comment.GetProperty("side").GetString()).IsEqualTo("RIGHT");
        await Assert.That(comment.TryGetProperty("new_position", out _)).IsFalse();
        using var status = JsonDocument.Parse(transport.Requests.ToArray()[1].Body!);
        await Assert.That(status.RootElement.GetProperty("description").GetString()!.Length).IsEqualTo(140);
        var rejected = false;
        try { await github.StatusAsync("TerraScale", "widgets", "head", new CommitStatus("skipped", null, "cancelled", "ts-reviewer"), CancellationToken.None); }
        catch (ArgumentException) { rejected = true; }
        await Assert.That(rejected).IsTrue();
        await Assert.That(transport.Requests.Count).IsEqualTo(2);
    }

    [Test]
    [Arguments("OtherOrg", "widgets")]
    [Arguments("TerraScale", "../outside")]
    [Arguments("TerraScale", "")]
    public async Task ClientRejectsRepositoriesOutsideOrganizationBeforeNetwork(string owner, string name)
    {
        var transport = new Transport();
        await Assert.That(await Client(transport).IsAllowedAsync(owner, name, CancellationToken.None)).IsFalse();
        await Assert.That(transport.Requests.Count).IsEqualTo(0);
    }

    [Test]
    public async Task AppJwtIsSignedAndInstallationTokenIsRepositoryScopedAndCached()
    {
        using var rsa = RSA.Create(2048);
        var keyPath = Path.Combine(Path.GetTempPath(), "reviewer-app-test-" + Guid.NewGuid().ToString("N") + ".pem");
        await File.WriteAllTextAsync(keyPath, rsa.ExportRSAPrivateKeyPem());
        var signed = false;
        var scoped = false;
        var tokenRequests = 0;
        var transport = new Transport(async request =>
        {
            if (request.RequestUri!.AbsolutePath == "/app/installations/456")
            {
                var pieces = request.Headers.Authorization!.Parameter!.Split('.');
                var signature = DecodeBase64Url(pieces[2]);
                signed = rsa.VerifyData(Encoding.ASCII.GetBytes(pieces[0] + "." + pieces[1]), signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                using var claims = JsonDocument.Parse(DecodeBase64Url(pieces[1]));
                signed &= claims.RootElement.GetProperty("iss").GetString() == "123"
                    && claims.RootElement.GetProperty("exp").GetInt64() - claims.RootElement.GetProperty("iat").GetInt64() <= 600;
                return Json(new { account = new { login = "TerraScale", type = "Organization" } });
            }
            if (request.RequestUri.AbsolutePath == "/app/installations/456/access_tokens")
            {
                tokenRequests++;
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                scoped = body.RootElement.GetProperty("repositories")[0].GetString() == "widgets"
                    && body.RootElement.GetProperty("permissions").GetProperty("contents").GetString() == "read";
                return Json(new { token = "fixture-installation-token", expires_at = DateTimeOffset.UtcNow.AddHours(1),
                    repositories = new[] { new { name = "widgets", owner = new { login = "TerraScale" } } } });
            }
            return Json(new { });
        });
        try
        {
            var github = new GitHubClient(new HttpClient(transport), Options() with { GitHubPrivateKeyPath = keyPath });
            await Assert.That(await github.IsAllowedAsync("TerraScale", "widgets", CancellationToken.None)).IsTrue();
            await github.CommentAsync("TerraScale", "widgets", 7, "evidence", CancellationToken.None);
            await Assert.That(signed).IsTrue();
            await Assert.That(scoped).IsTrue();
            await Assert.That(tokenRequests).IsEqualTo(1);
        }
        finally { File.Delete(keyPath); }
    }

    [Test]
    public async Task UnselectedInstallationRepositoryIsRejectedWithoutRepositoryApiRequest()
    {
        using var rsa = RSA.Create(2048);
        var keyPath = Path.GetTempFileName();
        await File.WriteAllTextAsync(keyPath, rsa.ExportRSAPrivateKeyPem());
        var transport = new Transport(request => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("access_tokens", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.UnprocessableEntity) { Content = new StringContent("unsafe echoed credential") }
            : Json(new { account = new { login = "TerraScale", type = "Organization" } })));
        try
        {
            var github = new GitHubClient(new HttpClient(transport), Options() with { GitHubPrivateKeyPath = keyPath });
            await Assert.That(await github.IsAllowedAsync("TerraScale", "unselected", CancellationToken.None)).IsFalse();
            await Assert.That(transport.Requests.Any(x => x.Path.StartsWith("/repos/", StringComparison.Ordinal))).IsFalse();
        }
        finally { File.Delete(keyPath); }
    }

    [Test]
    public async Task SynchronizeRoutesByEventAndDeduplicatesDeliveryAcrossHandlerRestart()
    {
        var database = Path.Combine(Path.GetTempPath(), "reviewer-github-" + Guid.NewGuid().ToString("N") + ".db");
        var payload = PullEvent("synchronize");
        var delivery = Guid.NewGuid().ToString("D");
        try
        {
            await using (var first = new WebhookFixture(database))
            {
                await Assert.That(await first.PostAsync("pull_request", payload, delivery)).IsEqualTo(202);
                await Assert.That(first.Queue.Snapshot()).IsEqualTo((1, 0));
            }
            await using (var restarted = new WebhookFixture(database))
            {
                await Assert.That(await restarted.PostAsync("pull_request", payload, delivery)).IsEqualTo(204);
                await Assert.That(restarted.Queue.Snapshot()).IsEqualTo((0, 0));
                await Assert.That(await restarted.PostAsync("issue_comment", PullEvent("opened"))).IsEqualTo(204);
                await Assert.That(await restarted.PostAsync("pull_request", PullEvent("synchronized"))).IsEqualTo(204);
            }
        }
        finally { File.Delete(database); }
    }

    [Test]
    [Arguments("read", "User", "author")]
    [Arguments("write", "Bot", "other-bot")]
    [Arguments("write", "User", "reviewer[bot]")]
    public async Task CommandsRejectReadersAndBots(string permission, string type, string login)
    {
        await using var fixture = new WebhookFixture(permission: permission);
        await Assert.That(await fixture.PostAsync("issue_comment", CommandEvent(login, type))).IsEqualTo(204);
        await Assert.That(fixture.Queue.Snapshot()).IsEqualTo((0, 0));
    }

    [Test]
    public async Task AuthorizedCommandIsIdempotentByCommentIdAndNewCommentCanRecheckSameHead()
    {
        await using var fixture = new WebhookFixture();
        await Assert.That(await fixture.PostAsync("issue_comment", CommandEvent())).IsEqualTo(202);
        await Assert.That(await fixture.PostAsync("issue_comment", CommandEvent())).IsEqualTo(204);
        await Assert.That(await fixture.PostAsync("issue_comment", CommandEvent(id: 2))).IsEqualTo(202);
        await Assert.That(fixture.Queue.Snapshot()).IsEqualTo((1, 0));
        await Assert.That(fixture.Transport.Requests.Count(x => x.Path.Contains("/collaborators/", StringComparison.Ordinal))).IsEqualTo(2);
    }

    [Test]
    public async Task FailedAuthorizationNetworkCallReleasesDeliveryForRetry()
    {
        await using var fixture = new WebhookFixture();
        fixture.PermissionFails = true;
        var delivery = Guid.NewGuid().ToString("D");
        await Assert.That(await fixture.PostAsync("issue_comment", CommandEvent(), delivery)).IsEqualTo(503);
        await Assert.That(fixture.Queue.Snapshot()).IsEqualTo((0, 0));
        fixture.PermissionFails = false;
        await Assert.That(await fixture.PostAsync("issue_comment", CommandEvent(), delivery)).IsEqualTo(202);
        await Assert.That(fixture.Queue.Snapshot()).IsEqualTo((1, 0));
    }

    [Test]
    public async Task DelayedClosedEventDoesNotCancelReopenedPullRequest()
    {
        await using var fixture = new WebhookFixture();
        await Assert.That(await fixture.PostAsync("pull_request", PullEvent("opened"))).IsEqualTo(202);
        await Assert.That(await fixture.PostAsync("pull_request", PullEvent("closed"))).IsEqualTo(204);
        await Assert.That(fixture.Queue.Snapshot()).IsEqualTo((1, 0));
        fixture.PullState = "closed";
        await Assert.That(await fixture.PostAsync("pull_request", PullEvent("closed"))).IsEqualTo(202);
        await Assert.That(fixture.Queue.Snapshot()).IsEqualTo((0, 0));
    }

    [Test]
    public async Task DelayedSynchronizeDoesNotReplaceTheCurrentHead()
    {
        await using var fixture = new WebhookFixture { PullHead = new string('b', 40) };
        await Assert.That(await fixture.PostAsync("pull_request", PullEvent("synchronize", fixture.PullHead))).IsEqualTo(202);
        await Assert.That(await fixture.PostAsync("pull_request", PullEvent("synchronize", new string('a', 40)))).IsEqualTo(204);
        await fixture.Queue.StartAsync(CancellationToken.None);
        await Assert.That(await fixture.ReviewedHead.Task.WaitAsync(TimeSpan.FromSeconds(5))).IsEqualTo(fixture.PullHead);
    }

    [Test]
    public async Task IgnoredOutdatedEventDoesNotConsumeAFutureCurrentHead()
    {
        await using var fixture = new WebhookFixture { PullHead = new string('b', 40) };
        var payload = PullEvent("synchronize", new string('a', 40));
        await Assert.That(await fixture.PostAsync("pull_request", payload)).IsEqualTo(204);
        fixture.PullHead = new string('a', 40);
        await Assert.That(await fixture.PostAsync("pull_request", payload)).IsEqualTo(202);
        await Assert.That(fixture.Queue.Snapshot()).IsEqualTo((1, 0));
    }

    [Test]
    [Arguments("success")]
    [Arguments("failure")]
    [Arguments("error")]
    public async Task CompletedOutcomeDeduplicatesAutomaticHeadWithNewDeliveryId(string state)
    {
        await using var fixture = new WebhookFixture();
        await Assert.That(await fixture.PostAsync("pull_request", PullEvent("synchronize"))).IsEqualTo(202);
        await fixture.Queue.StartAsync(CancellationToken.None);
        await fixture.ReviewedHead.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.HeadStatus = state;
        fixture.StatusDescription = state == "error" ? "review incomplete: provider failed" : "Review finished";
        await Assert.That(await fixture.PostAsync("pull_request", PullEvent("synchronize"))).IsEqualTo(204);
        await Assert.That(fixture.Transport.Requests.Count(x => x.Path.EndsWith("/status", StringComparison.Ordinal))).IsEqualTo(2);
    }

    [Test]
    public async Task CancelledOutcomeCanBeReviewedWhenTheHeadBecomesCurrentAgain()
    {
        await using var fixture = new WebhookFixture { HeadStatus = "error", StatusDescription = "Review cancelled: a newer push replaced this head" };
        await Assert.That(await fixture.PostAsync("pull_request", PullEvent("synchronize"))).IsEqualTo(202);
        await Assert.That(fixture.Queue.Snapshot()).IsEqualTo((1, 0));
    }

    [Test]
    public async Task ForcePushBackToCancelledHeadRequeuesBeforeOldRunnerStops()
    {
        var a = new string('a', 40);
        var b = new string('b', 40);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repeated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = new ConcurrentQueue<string>();
        var aRuns = 0;
        await using var fixture = new WebhookFixture(runner: async (request, ct) =>
        {
            runs.Enqueue(request.HeadSha!);
            if (request.HeadSha != a) return;
            if (Interlocked.Increment(ref aRuns) == 2) { repeated.TrySetResult(); return; }
            started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                await release.Task;
                throw;
            }
        });
        try
        {
            await Assert.That(await fixture.PostAsync("pull_request", PullEvent("synchronize", a))).IsEqualTo(202);
            await fixture.Queue.StartAsync(CancellationToken.None);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.PullHead = b;
            await Assert.That(await fixture.PostAsync("pull_request", PullEvent("synchronize", b))).IsEqualTo(202);
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.PullHead = a;
            await Assert.That(await fixture.PostAsync("pull_request", PullEvent("synchronize", a))).IsEqualTo(202);
            release.TrySetResult();
            await repeated.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(string.Join(',', runs)).IsEqualTo(a + "," + a);
        }
        finally { release.TrySetResult(); }
    }

    [Test]
    public async Task TruncatedTreeCannotProduceACompleteContextIndex()
    {
        var transport = new Transport(_ => Task.FromResult(Json(new { tree = Array.Empty<object>(), truncated = true })));
        var rejected = false;
        try { await Client(transport).TreeAsync("TerraScale", "widgets", new string('a', 40), CancellationToken.None); }
        catch (InvalidOperationException exception) { rejected = exception.Message.Contains("incomplete", StringComparison.Ordinal); }
        await Assert.That(rejected).IsTrue();
    }

    private static object PullEvent(string action, string? head = null) => new { action, number = 7, repository = new { name = "widgets", owner = new { login = "TerraScale" } },
        pull_request = new { number = 7, head = new { sha = head ?? new string('a', 40) } } };
    private static object CommandEvent(string login = "author", string type = "User", long id = 1) => new { action = "created",
        repository = new { name = "widgets", owner = new { login = "TerraScale" } }, issue = new { number = 7, pull_request = new { } },
        comment = new { id, body = "@reviewer[bot] recheck", user = new { login, type } } };
    private static GitHubClient Client(Transport transport) => new(new HttpClient(transport), Options(), (_, _, _) => Task.FromResult("fixture-installation-token"));
    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
    private static byte[] DecodeBase64Url(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));

    private sealed record Request(string Path, string? Body, bool Authenticated);
    private sealed class Transport(Func<HttpRequestMessage, Task<HttpResponseMessage>>? respond = null) : HttpMessageHandler
    {
        public ConcurrentQueue<Request> Requests { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Enqueue(new Request(request.RequestUri!.AbsolutePath, body,
                request.Headers.Authorization?.Scheme == "Bearer"));
            return respond is null ? Json(new { }) : await respond(request);
        }
    }

    private sealed class WebhookFixture : IAsyncDisposable
    {
        private readonly ContextStore _store;
        private readonly string _database;
        private readonly bool _delete;
        public bool PermissionFails { get; set; }
        public string PullState { get; set; } = "open";
        public string PullHead { get; set; } = new string('a', 40);
        public string? HeadStatus { get; set; }
        public string? StatusDescription { get; set; }
        public TaskCompletionSource<string> ReviewedHead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Transport Transport { get; }
        public ReviewQueue Queue { get; }
        public WebhookHandler Handler { get; }
        public WebhookFixture(string? database = null, string permission = "write", Func<ReviewRequest, CancellationToken, Task>? runner = null)
        {
            _database = database ?? Path.Combine(Path.GetTempPath(), "reviewer-webhook-" + Guid.NewGuid().ToString("N") + ".db");
            _delete = database is null;
            var options = Options(_database);
            Transport = new Transport(request =>
            {
                if (request.RequestUri!.AbsolutePath.Contains("/collaborators/", StringComparison.Ordinal))
                    return Task.FromResult(PermissionFails ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Json(new { permission }));
                if (request.RequestUri.AbsolutePath.EndsWith("/status", StringComparison.Ordinal))
                    return Task.FromResult(Json(new CombinedStatus(HeadStatus is null ? [] :
                        [new ContextStatus(PullRequestReviewer.StatusContext, HeadStatus, StatusDescription)])));
                if (request.RequestUri.AbsolutePath.EndsWith("/pulls/7", StringComparison.Ordinal))
                    return Task.FromResult(Json(new PullRequest(7, "title", "body", new Branch(new string('b', 40), "main", new Repository(new Owner("TerraScale"), "widgets")),
                        new Branch(PullHead, "feature", new Repository(new Owner("TerraScale"), "widgets")), PullState)));
                return Task.FromResult(Json(new { }));
            });
            var github = new GitHubClient(new HttpClient(Transport), options, (_, _, _) => Task.FromResult("fixture-installation-token"));
            Queue = new ReviewQueue(runner ?? ((request, _) => { ReviewedHead.TrySetResult(request.HeadSha!); return Task.CompletedTask; }),
                (_, _) => Task.CompletedTask, 1, NullLogger<ReviewQueue>.Instance);
            _store = new ContextStore(options);
            var context = new ContextIndexer(github, _store, Embedder, options, NullLogger<ContextIndexer>.Instance);
            var reviewer = new PullRequestReviewer(github, new ReviewEngine(options, NullLogger<ReviewEngine>.Instance), options, NullLogger<PullRequestReviewer>.Instance);
            Handler = new WebhookHandler(github, Queue, reviewer, context, options, NullLogger<WebhookHandler>.Instance);
        }
        public async Task<int?> PostAsync(string eventName, object payload, string? delivery = null)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
            var context = new DefaultHttpContext();
            context.Request.Method = "POST";
            context.Request.Headers["X-Hub-Signature-256"] = "sha256=" + Convert.ToHexString(HMACSHA256.HashData("webhook-test-secret"u8.ToArray(), bytes));
            context.Request.Headers["X-GitHub-Event"] = eventName;
            context.Request.Headers["X-GitHub-Delivery"] = delivery ?? Guid.NewGuid().ToString("D");
            context.Request.Body = new MemoryStream(bytes);
            return ((IStatusCodeHttpResult)await Handler.HandleAsync(context.Request, CancellationToken.None)).StatusCode;
        }
        public async ValueTask DisposeAsync()
        {
            await Queue.StopAsync(CancellationToken.None);
            _store.Dispose();
            if (_delete) File.Delete(_database);
        }
    }
}
