using TerraScale.Reviewer.Features.Forgejo;
using TerraScale.Reviewer.Features.Configuration;
using TerraScale.Reviewer.Features.Context;
using TerraScale.Reviewer.Features.Review;
using TerraScale.Reviewer.Features.Webhooks;
using TUnit.Assertions;
using TUnit.Core;
using System.Security.Cryptography;
using System.Text;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

public sealed class ReviewerTests
{
    private static readonly ContextEmbedder Embedder = new();

    private static ReviewerOptions Options(string database) =>
        new("http://+:8080", "https://forgejo.test", "forgejo", "https://proxy.test/v1", "proxy", "review", "TerraScale", "bot", 2, TimeSpan.FromMinutes(1), 100000, "", "", database, 100);

    private static float[] UnitVector(int index)
    {
        var vector = new float[Embedder.Embed("dimension probe").Length];
        vector[index] = 1;
        return vector;
    }

    [Test]
    public async Task SignatureAndCommandValidation()
    {
        await Assert.That(WebhookHandler.ValidSignature("body"u8, "bad", "secret")).IsFalse();
        var signature = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("secret"), "body"u8));
        await Assert.That(WebhookHandler.ValidSignature("body"u8, "sha256=" + signature, "secret")).IsTrue();
        await Assert.That(WebhookHandler.ParseCommand("please @terrascale-bot recheck", "terrascale-bot")).IsEqualTo("recheck");
        await Assert.That(WebhookHandler.ParseCommand("@other-bot review", "terrascale-bot")).IsNull();
        await Assert.That(WebhookHandler.ParseCommand("@terrascale-bot dismiss now", "terrascale-bot")).IsEqualTo("dismiss");
    }

    [Test]
    public async Task UnifiedDiffMapsAddedLines()
    {
        var raw = "diff --git a/main.go b/main.go\n@@ -1,2 +1,3 @@\n package main\n+var value = 1\n func main() {}\n";
        var files = UnifiedDiff.Parse(raw);
        await Assert.That(files.Count).IsEqualTo(1);
        await Assert.That(files[0].Filename).IsEqualTo("main.go");
        await Assert.That(UnifiedDiff.ReviewPosition(files[0].Patch, 2)).IsGreaterThan(0);
        await Assert.That(UnifiedDiff.ReviewPosition(files[0].Patch, 99)).IsEqualTo(0);
    }

    [Test]
    public async Task UnifiedDiffKeepsMultipleFilesAndDeletedLinesOutOfNewLineMapping()
    {
        var raw = "diff --git a/one.cs b/one.cs\n@@ -1,3 +1,2 @@\n-old line\n kept\n+new line\n" +
                  "diff --git a/two.cs b/two.cs\n@@ -1 +1 @@\n-old\n+new\n";
        var files = UnifiedDiff.Parse(raw);
        await Assert.That(files.Count).IsEqualTo(2);
        await Assert.That(files[0].Filename).IsEqualTo("one.cs");
        await Assert.That(files[1].Filename).IsEqualTo("two.cs");
        await Assert.That(UnifiedDiff.ReviewPosition(files[0].Patch, 2)).IsGreaterThan(0);
    }

    [Test]
    public async Task ConfigurationAcceptsExplicitArgumentsAndRejectsInvalidLimits()
    {
        var options = ReviewerOptions.Load(["--forgejo-token=forgejo", "--proxy-token=proxy", "--forgejo-url=https://forgejo.test/", "--proxy-url=https://proxy.test/v1/", "--timeout=00:00:05", "--max-concurrent=3"]);
        await Assert.That(options.ForgejoUrl).IsEqualTo("https://forgejo.test");
        await Assert.That(options.ProxyUrl).IsEqualTo("https://proxy.test/v1");
        await Assert.That(options.MaxConcurrent).IsEqualTo(3);

        var rejected = false;
        try { ReviewerOptions.Load(["--forgejo-token=forgejo", "--proxy-token=proxy", "--max-concurrent=0"]); }
        catch (InvalidOperationException) { rejected = true; }
        await Assert.That(rejected).IsTrue();
    }

    [Test]
    public async Task MergeDropsLowConfidenceAndDuplicates()
    {
        var high = new Finding("a", "high", "title", "body", "fix", "x.go", 4, .99, false, "");
        var low = high with { Id = "b", Confidence = .5 };
        var other = high with { Id = "c", Confidence = .8 };
        var result = ReviewEngine.Merge([new ReviewResult("one", [high, low]), new ReviewResult("two", [high, other])]);
        await Assert.That(result.Findings.Count).IsEqualTo(2);
    }

    [Test]
    public async Task ReviewCommentIsStructuredForFindingsAndCleanForEmptyReviews()
    {
        var repository = new Repository(new Owner("TerraScale"), "ts-reviewer");
        var branch = new Branch("abc123", "main", repository);
        var pullRequest = new PullRequest(14, "Improve review output", "", branch, branch);
        var empty = WebhookHandler.Format(pullRequest, new ReviewResult("ignored", []));
        await Assert.That(empty.Contains("### Summary")).IsTrue();
        await Assert.That(empty.Contains("### Result")).IsTrue();
        await Assert.That(empty.Contains("✅ No evidence-backed findings")).IsTrue();

        var finding = new Finding("f-1", "high", "Unsafe fallback", "The fallback bypasses validation.", "Add a validation check before retrying.", "src/Worker.cs", 42, .99, false, "");
        var formatted = WebhookHandler.Format(pullRequest, new ReviewResult("ignored", [finding]));
        await Assert.That(formatted.Contains("### Findings (1)")).IsTrue();
        await Assert.That(formatted.Contains("#### HIGH · Unsafe fallback")).IsTrue();
        await Assert.That(formatted.Contains("<summary>Suggested fix</summary>")).IsTrue();
    }

    [Test]
    public async Task LiteDbPersistsAndSearchesExactCommitContext()
    {
        var database = Path.Combine(Path.GetTempPath(), "ts-reviewer-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var store = new ContextStore(Options(database));
            var chunk = new CodeChunk("src/Widget.cs", 10, 20, "CreateWidget", "public Widget CreateWidget() => new();");
            store.ReplaceCommit("TerraScale/widgets", "abc123", [(chunk, UnitVector(0))]);
            var results = store.Search("TerraScale/widgets", "abc123", UnitVector(0), 5);
            await Assert.That(results).IsNotEmpty();
            await Assert.That(results[0].Path).IsEqualTo("src/Widget.cs");
            await Assert.That(store.IsIndexed("TerraScale/widgets", "abc123")).IsTrue();
        }
        finally { File.Delete(database); }
    }

    [Test]
    public async Task LiteDbContextSearchNeverLeaksAnotherCommit()
    {
        var database = Path.Combine(Path.GetTempPath(), "ts-reviewer-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var store = new ContextStore(Options(database));
            store.ReplaceCommit("TerraScale/widgets", "old", [(new CodeChunk("old.cs", 1, 1, "Old", "old implementation"), UnitVector(0))]);
            store.ReplaceCommit("TerraScale/widgets", "new", [(new CodeChunk("new.cs", 1, 1, "New", "new implementation"), UnitVector(1))]);
            var oldResults = store.Search("TerraScale/widgets", "old", UnitVector(0), 5);
            var newResults = store.Search("TerraScale/widgets", "new", UnitVector(1), 5);
            await Assert.That(oldResults.Count).IsEqualTo(1);
            await Assert.That(oldResults[0].Path).IsEqualTo("old.cs");
            await Assert.That(newResults.Count).IsEqualTo(1);
            await Assert.That(newResults[0].Path).IsEqualTo("new.cs");
        }
        finally { File.Delete(database); }
    }

    [Test]
    public async Task LiteDbContextReplacementRemovesOldChunks()
    {
        var database = Path.Combine(Path.GetTempPath(), "ts-reviewer-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var store = new ContextStore(Options(database));
            store.ReplaceCommit("TerraScale/widgets", "same", [(new CodeChunk("before.cs", 1, 1, "Before", "before marker"), UnitVector(0))]);
            store.ReplaceCommit("TerraScale/widgets", "same", [(new CodeChunk("after.cs", 1, 1, "After", "after marker"), UnitVector(0))]);
            var results = store.Search("TerraScale/widgets", "same", UnitVector(0), 5);
            await Assert.That(results.Count).IsEqualTo(1);
            await Assert.That(results[0].Path).IsEqualTo("after.cs");
        }
        finally { File.Delete(database); }
    }

    [Test]
    public async Task ContextIndexerFetchesExactCommitAndEmbedsChunksLocally()
    {
        var database = Path.Combine(Path.GetTempPath(), "ts-reviewer-" + Guid.NewGuid().ToString("N") + ".db");
        var options = Options(database);
        var handler = new ContextFakeHandler();
        var forgejo = new ForgejoClient(new HttpClient(handler), options);
        using var store = new ContextStore(options);
        var indexer = new ContextIndexer(forgejo, store, Embedder, options, NullLogger<ContextIndexer>.Instance);
        try
        {
            await indexer.StartAsync(CancellationToken.None);
            await indexer.EnsureIndexedAsync("TerraScale", "widgets", "abc123", CancellationToken.None);
            var results = store.Search("TerraScale/widgets", "abc123", Embedder.Embed("CreateWidget"), 5);
            await Assert.That(results.Count).IsEqualTo(1);
            await Assert.That(results[0].Path).IsEqualTo("src/Widget.cs");
            await Assert.That(handler.FileRequests).IsEqualTo(1);
            await indexer.StopAsync(CancellationToken.None);
        }
        finally { File.Delete(database); }
    }

    [Test]
    public async Task WebhookAddsEyesReactionWhenPullRequestOrCommandIsSeen()
    {
        var database = Path.Combine(Path.GetTempPath(), "ts-reviewer-" + Guid.NewGuid().ToString("N") + ".db");
        var options = Options(database);
        var handler = new AcknowledgeFakeHandler();
        var forgejo = new ForgejoClient(new HttpClient(handler), options);
        using var store = new ContextStore(options);
        var indexer = new ContextIndexer(forgejo, store, Embedder, options, NullLogger<ContextIndexer>.Instance);
        var engine = new ReviewEngine(new HttpClient(handler), options, indexer, NullLogger<ReviewEngine>.Instance);
        var webhookHandler = new WebhookHandler(forgejo, engine, indexer, options, NullLogger<WebhookHandler>.Instance);
        try
        {
            var result = await PostWebhookAsync(webhookHandler, new
            {
                action = "opened",
                number = 14,
                repository = new { name = "widgets", owner = new { login = "TerraScale" } },
                pull_request = new { number = 14 },
            });
            await Assert.That(((IStatusCodeHttpResult)result).StatusCode).IsEqualTo(StatusCodes.Status202Accepted);
            await WaitUntilAsync(() => handler.PullRequestReactions == 1);

            await PostWebhookAsync(webhookHandler, new
            {
                action = "created",
                number = 14,
                repository = new { name = "widgets", owner = new { login = "TerraScale" } },
                pull_request = new { number = 14 },
                comment = new { id = 77, body = "@bot recheck" },
            });
            await WaitUntilAsync(() => handler.CommentReactions == 1);
            await Assert.That(handler.PullRequestReactions).IsEqualTo(1);
        }
        finally { File.Delete(database); }
    }

    private static async Task<IResult> PostWebhookAsync(WebhookHandler handler, object payload)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(payload));
        return await handler.HandleAsync(context.Request, CancellationToken.None);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!condition() && !timeout.Token.IsCancellationRequested) await Task.Delay(25);
        await Assert.That(condition()).IsTrue();
    }

    private sealed class ContextFakeHandler : HttpMessageHandler
    {
        public int FileRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath.Contains("/git/trees/", StringComparison.Ordinal) == true)
                return Task.FromResult(Json(new { tree = new[] { new { path = "src/Widget.cs", type = "blob", size = 58, sha = "file-sha" } } }));
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath.Contains("/contents/", StringComparison.Ordinal) == true)
            {
                FileRequests++;
                var content = Convert.ToBase64String(Encoding.UTF8.GetBytes("public class Widget { public Widget CreateWidget() => new(); }"));
                return Task.FromResult(Json(new { content, encoding = "base64", type = "file" }));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }

    private sealed class AcknowledgeFakeHandler : HttpMessageHandler
    {
        public int PullRequestReactions { get; private set; }
        public int CommentReactions { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath.EndsWith("/issues/14/reactions", StringComparison.Ordinal) == true)
                PullRequestReactions++;
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath.EndsWith("/issues/comments/77/reactions", StringComparison.Ordinal) == true)
                CommentReactions++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
