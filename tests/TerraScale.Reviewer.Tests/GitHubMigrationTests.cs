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
        "https://glm.test", "test-key", "TerraScale", "reviewer[bot]", "webhook-test-secret", "context-test-token", database,
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
        var actionsScoped = false;
        var dispatchBound = false;
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
                var permissions = body.RootElement.GetProperty("permissions");
                if (permissions.TryGetProperty("contents", out var contents))
                    scoped = body.RootElement.GetProperty("repositories")[0].GetString() == "widgets"
                        && contents.GetString() == "read" && !permissions.TryGetProperty("actions", out _);
                else actionsScoped = permissions.GetProperty("actions").GetString() == "write"
                    && permissions.GetProperty("metadata").GetString() == "read" && permissions.EnumerateObject().Count() == 2;
                return Json(new { token = "fixture-installation-token", expires_at = DateTimeOffset.UtcNow.AddHours(1),
                    repositories = new[] { new { name = "widgets", owner = new { login = "TerraScale" } } } });
            }
            if (request.RequestUri.AbsolutePath.EndsWith("/dispatches", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                dispatchBound = body.RootElement.GetProperty("ref").GetString() == "main"
                    && body.RootElement.GetProperty("inputs").GetProperty("expected_commit").GetString() == new string('a', 40);
            }
            return Json(new { });
        });
        try
        {
            var github = new GitHubClient(new HttpClient(transport), Options() with { GitHubPrivateKeyPath = keyPath });
            await Assert.That(await github.IsAllowedAsync("TerraScale", "widgets", CancellationToken.None)).IsTrue();
            await github.CommentAsync("TerraScale", "widgets", 7, "evidence", CancellationToken.None);
            await github.WorkflowAsync("TerraScale", "widgets", "ci.yml", CancellationToken.None);
            await github.DispatchWorkflowAsync("TerraScale", "widgets", "ci.yml", "main", new string('a', 40), CancellationToken.None);
            await Assert.That(signed).IsTrue();
            await Assert.That(scoped).IsTrue();
            await Assert.That(actionsScoped).IsTrue();
            await Assert.That(dispatchBound).IsTrue();
            await Assert.That(tokenRequests).IsEqualTo(2);
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
        await Assert.That(await fixture.PostAsync("pull_request", PullEvent("closed"))).IsEqualTo(202);
        await Assert.That(fixture.Queue.Snapshot()).IsEqualTo((1, 0));
        fixture.PullState = "closed";
        await Assert.That(await fixture.PostAsync("pull_request", PullEvent("closed"))).IsEqualTo(202);
        await Assert.That(fixture.Queue.Snapshot()).IsEqualTo((0, 0));
    }

    [Test]
    public async Task DelayedSynchronizeResolvesTheActualCurrentHeadWithoutAnotherPaidReview()
    {
        await using var fixture = new WebhookFixture(realReviewer: true) { PullHead = new string('b', 40), HeadStatus = "success" };
        await Assert.That(await fixture.PostAsync("pull_request", PullEvent("synchronize", fixture.PullHead))).IsEqualTo(202);
        await Assert.That(await fixture.PostAsync("pull_request", PullEvent("synchronize", new string('a', 40)))).IsEqualTo(202);
        await fixture.Queue.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => fixture.Queue.Snapshot() == (0, 0));
        await Assert.That(fixture.Transport.Requests.Any(x => x.Path.EndsWith("/commits/" + fixture.PullHead + "/statuses", StringComparison.Ordinal))).IsTrue();
        await Assert.That(fixture.Transport.Requests.Any(x => x.Path.EndsWith("/reviews", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    [Arguments("success")]
    [Arguments("failure")]
    [Arguments("error")]
    public async Task CompletedOwnOutcomeIsCheckedBeforeAnyPendingOverwrite(string state)
    {
        await using var fixture = new WebhookFixture(realReviewer: true) { HeadStatus = state, StatusDescription = "Review finished" };
        await Assert.That(await fixture.PostAsync("pull_request", PullEvent("synchronize"))).IsEqualTo(202);
        await fixture.Queue.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => fixture.Queue.Snapshot() == (0, 0));
        await Assert.That(await fixture.PostAsync("pull_request", PullEvent("synchronize"))).IsEqualTo(202);
        await WaitUntilAsync(() => fixture.Queue.Snapshot() == (0, 0));
        await Assert.That(fixture.Transport.Requests.Count(x => x.Path.EndsWith("/statuses", StringComparison.Ordinal))).IsEqualTo(2);
        await Assert.That(fixture.Transport.Requests.Any(x => x.Path.Contains("/statuses/", StringComparison.Ordinal) || x.Path.EndsWith("/reviews", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    [Arguments("outsider", 7, 'b')]
    [Arguments("reviewer[bot]", 8, 'b')]
    [Arguments("reviewer[bot]", 7, 'c')]
    public async Task SpoofedOrDifferentPullRequestOrBaseStatusCannotSuppressReview(string creator, int number, char baseCharacter)
    {
        await using var fixture = new WebhookFixture { HeadStatus = "success", StatusCreator = creator, StatusNumber = number, StatusBase = new string(baseCharacter, 40) };
        var status = await fixture.Reviewer.LatestOwnedStatusAsync(fixture.Metadata, "TerraScale", "widgets", CancellationToken.None);
        await Assert.That(status).IsNull();
        await Assert.That(PullRequestReviewer.IsCompleted(status, fixture.Metadata)).IsFalse();
    }

    [Test]
    public async Task CancelledOwnOutcomeCanBeReviewedWhenTheHeadBecomesCurrentAgain()
    {
        await using var fixture = new WebhookFixture { HeadStatus = "error", StatusDescription = "Review cancelled: a newer push replaced this head" };
        var status = await fixture.Reviewer.LatestOwnedStatusAsync(fixture.Metadata, "TerraScale", "widgets", CancellationToken.None);
        await Assert.That(status).IsNotNull();
        await Assert.That(PullRequestReviewer.IsCompleted(status, fixture.Metadata)).IsFalse();
    }

    private static async Task WaitUntilAsync(Func<bool> done)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!done()) await Task.Delay(10, timeout.Token);
    }

    [Test]
    public async Task AutomaticAdmissionPreservesOrderWhileAuthenticationIsBlocked()
    {
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new WebhookFixture(tokenFactory: (_, _, _) => release.Task);
        try
        {
            var a = new string('a', 40);
            var b = new string('b', 40);
            await Assert.That(await fixture.PostAsync("pull_request", PullEvent("synchronize", a)).WaitAsync(TimeSpan.FromSeconds(1))).IsEqualTo(202);
            await Assert.That(await fixture.PostAsync("pull_request", PullEvent("synchronize", b)).WaitAsync(TimeSpan.FromSeconds(1))).IsEqualTo(202);
            await fixture.Queue.StartAsync(CancellationToken.None);
            await Assert.That(await fixture.ReviewedHead.Task.WaitAsync(TimeSpan.FromSeconds(5))).IsEqualTo(b);
        }
        finally { release.TrySetResult("fixture-installation-token"); }
    }

    [Test]
    public async Task MetadataTransientFailureRetriesWithoutLosingAcceptedHead()
    {
        var attempts = 0;
        var repository = new Repository(new Owner("TerraScale"), "widgets");
        var transport = new Transport(_ => Task.FromResult(++attempts == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : Json(new PullRequest(7, "title", "", new Branch(new string('b', 40), "main", repository), new Branch(new string('a', 40), "head", repository)))));
        var pull = await Client(transport).PullAsync("TerraScale", "widgets", 7, CancellationToken.None);
        await Assert.That(pull?.Head.Sha).IsEqualTo(new string('a', 40));
        await Assert.That(attempts).IsEqualTo(2);
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

    [Test]
    public async Task CancellationCannotOverwriteACompletedOwnedOutcome()
    {
        await using var fixture = new WebhookFixture { HeadStatus = "success" };
        await fixture.Reviewer.SkipAsync(new ReviewRequest("TerraScale", "widgets", 7, fixture.PullHead, BaseSha: fixture.Metadata.Base.Sha), ReviewQueue.NewerHead);
        await Assert.That(fixture.Transport.Requests.Any(x => x.Path.Contains("/statuses/", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task StatusPaginationFindsOwnedOutcomeBehindSpoofedContexts()
    {
        var baseSha = new string('b', 40);
        var spoofed = new ContextStatus("ts-reviewer", "success", PullRequestReviewer.StatusDescription(7, baseSha, "spoof"), new Owner("outsider"));
        var owned = spoofed with { Creator = new Owner("reviewer[bot]") };
        var transport = new Transport(request => Task.FromResult(Json(request.RequestUri!.Query.EndsWith("&page=1", StringComparison.Ordinal)
            ? Enumerable.Repeat(spoofed, 100).ToArray() : new[] { owned })));
        var options = Options();
        var reviewer = new PullRequestReviewer(Client(transport), new ReviewEngine(options, NullLogger<ReviewEngine>.Instance), options, NullLogger<PullRequestReviewer>.Instance);
        var repo = new Repository(new Owner("TerraScale"), "widgets");
        var pull = new PullRequest(7, "title", "", new Branch(baseSha, "main", repo), new Branch(new string('a', 40), "head", repo));
        var status = await reviewer.LatestOwnedStatusAsync(pull, "TerraScale", "widgets", CancellationToken.None);
        await Assert.That(status?.Creator?.Login).IsEqualTo("reviewer[bot]");
        await Assert.That(transport.Requests.Count).IsEqualTo(2);
    }

    [Test]
    public async Task ArchiveRedirectDoesNotForwardInstallationAuthorization()
    {
        var transport = new Transport(request => Task.FromResult(request.RequestUri!.Host == "github.test"
            ? Redirect("https://codeload.github.com/TerraScale/widgets/tar.gz/" + new string('a', 40))
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("fixture archive") }));
        using var response = await Client(transport).ArchiveAsync("TerraScale", "widgets", new string('a', 40), CancellationToken.None);
        await Assert.That(response.IsSuccessStatusCode).IsTrue();
        var requests = transport.Requests.ToArray();
        await Assert.That(requests[0].Authenticated).IsTrue();
        await Assert.That(requests[1].Authenticated).IsFalse();
    }

    [Test]
    [Arguments("https://outside.test/TerraScale/widgets/tar.gz/sha")]
    [Arguments("http://codeload.github.com/TerraScale/widgets/tar.gz/sha")]
    [Arguments("https://codeload.github.com:444/TerraScale/widgets/tar.gz/sha")]
    [Arguments("https://codeload.github.com/OtherOrg/widgets/tar.gz/sha")]
    [Arguments("https://codeload.github.com/TerraScale/other/tar.gz/sha")]
    [Arguments("https://user@codeload.github.com/TerraScale/widgets/tar.gz/sha")]
    public async Task ArchiveRejectsRedirectOutsideTrustedRepositoryBoundary(string location)
    {
        var transport = new Transport(_ => Task.FromResult(Redirect(location)));
        var rejected = false;
        try { using var response = await Client(transport).ArchiveAsync("TerraScale", "widgets", new string('a', 40), CancellationToken.None); }
        catch (InvalidOperationException) { rejected = true; }
        await Assert.That(rejected).IsTrue();
        await Assert.That(transport.Requests.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ArchiveFollowsOnlyOneRedirect()
    {
        var transport = new Transport(_ => Task.FromResult(Redirect("https://codeload.github.com/TerraScale/widgets/tar.gz/sha")));
        var rejected = false;
        try { using var response = await Client(transport).ArchiveAsync("TerraScale", "widgets", new string('a', 40), CancellationToken.None); }
        catch (HttpRequestException ex) { rejected = ex.StatusCode == HttpStatusCode.Found; }
        await Assert.That(rejected).IsTrue();
        await Assert.That(transport.Requests.Count).IsEqualTo(2);
    }

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location);
        return response;
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
        public string StatusCreator { get; set; } = "reviewer[bot]";
        public int StatusNumber { get; set; } = 7;
        public string? StatusBase { get; set; }
        public PullRequestReviewer Reviewer { get; }
        public PullRequest Metadata => new(7, "title", "body", new Branch(new string('b', 40), "main", new Repository(new Owner("TerraScale"), "widgets")),
            new Branch(PullHead, "feature", new Repository(new Owner("TerraScale"), "widgets")), PullState);
        public TaskCompletionSource<string> ReviewedHead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Transport Transport { get; }
        public ReviewQueue Queue { get; }
        public WebhookHandler Handler { get; }
        public WebhookFixture(string? database = null, string permission = "write", Func<ReviewRequest, CancellationToken, Task>? runner = null,
            bool realReviewer = false, Func<string, string, CancellationToken, Task<string>>? tokenFactory = null)
        {
            _database = database ?? Path.Combine(Path.GetTempPath(), "reviewer-webhook-" + Guid.NewGuid().ToString("N") + ".db");
            _delete = database is null;
            var options = Options(_database);
            Transport = new Transport(request =>
            {
                if (request.RequestUri!.AbsolutePath.Contains("/collaborators/", StringComparison.Ordinal))
                    return Task.FromResult(PermissionFails ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Json(new { permission }));
                if (request.RequestUri.AbsolutePath.EndsWith("/statuses", StringComparison.Ordinal))
                    return Task.FromResult(Json(HeadStatus is null ? Array.Empty<ContextStatus>() :
                        new[] { new ContextStatus(PullRequestReviewer.StatusContext, HeadStatus,
                            PullRequestReviewer.StatusDescription(StatusNumber, StatusBase ?? Metadata.Base.Sha, StatusDescription ?? "Review complete"), new Owner(StatusCreator)) }));
                if (request.RequestUri.AbsolutePath.EndsWith("/pulls/7", StringComparison.Ordinal))
                    return Task.FromResult(Json(Metadata));
                return Task.FromResult(Json(new { }));
            });
            var github = new GitHubClient(new HttpClient(Transport), options, tokenFactory ?? ((_, _, _) => Task.FromResult("fixture-installation-token")));
            _store = new ContextStore(options);
            var context = new ContextIndexer(github, _store, Embedder, options, NullLogger<ContextIndexer>.Instance);
            Reviewer = new PullRequestReviewer(github, new ReviewEngine(options, NullLogger<ReviewEngine>.Instance), options, NullLogger<PullRequestReviewer>.Instance);
            Queue = realReviewer ? new ReviewQueue(Reviewer, NullLogger<ReviewQueue>.Instance)
                : new ReviewQueue(runner ?? ((request, _) => { ReviewedHead.TrySetResult(request.HeadSha!); return Task.CompletedTask; }),
                    (_, _) => Task.CompletedTask, 1, NullLogger<ReviewQueue>.Instance);
            Handler = new WebhookHandler(github, Queue, Reviewer, context, options, NullLogger<WebhookHandler>.Instance);
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
