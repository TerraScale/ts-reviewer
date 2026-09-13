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
using Microsoft.Extensions.Logging.Abstractions;

public sealed class ReviewerTests
{
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
        var options = ReviewerOptions.Load(["--forgejo-token=forgejo", "--proxy-token=proxy", "--forgejo-url=https://forgejo.test/", "--proxy-url=https://proxy.test/v1/", "--timeout=00:00:05", "--max-concurrent=3", "--context-embedding-dimensions=8"]);
        await Assert.That(options.ForgejoUrl).IsEqualTo("https://forgejo.test");
        await Assert.That(options.ProxyUrl).IsEqualTo("https://proxy.test/v1");
        await Assert.That(options.MaxConcurrent).IsEqualTo(3);
        await Assert.That(options.ContextEmbeddingDimensions).IsEqualTo(8);

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
    public async Task SqliteVecPersistsAndSearchesExactCommitContext()
    {
        var database = Path.Combine(Path.GetTempPath(), "ts-reviewer-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var options = new ReviewerOptions("http://+:8080", "https://forgejo.test", "forgejo", "https://proxy.test/v1", "proxy", "review", "embed", "TerraScale", "bot", 2, TimeSpan.FromMinutes(1), 100000, "", "", database, 100, 8);
            var store = new ContextStore(options);
            var chunk = new CodeChunk("src/Widget.cs", 10, 20, "CreateWidget", "public Widget CreateWidget() => new();");
            await store.ReplaceCommitAsync("TerraScale/widgets", "abc123", [(chunk, new float[] { 1, 0, 0, 0, 0, 0, 0, 0 })], CancellationToken.None);
            var results = await store.SearchAsync("TerraScale/widgets", "abc123", "CreateWidget", 5, new float[] { 1, 0, 0, 0, 0, 0, 0, 0 }, CancellationToken.None);
            await Assert.That(results).IsNotEmpty();
            await Assert.That(results[0].Path).IsEqualTo("src/Widget.cs");
            await Assert.That(await store.IsIndexedAsync("TerraScale/widgets", "abc123", CancellationToken.None)).IsTrue();
        }
        finally { File.Delete(database); }
    }

    [Test]
    public async Task SqliteContextSearchNeverLeaksAnotherCommit()
    {
        var database = Path.Combine(Path.GetTempPath(), "ts-reviewer-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var options = new ReviewerOptions("http://+:8080", "https://forgejo.test", "forgejo", "https://proxy.test/v1", "proxy", "review", "embed", "TerraScale", "bot", 2, TimeSpan.FromMinutes(1), 100000, "", "", database, 100, 8);
            var store = new ContextStore(options);
            await store.ReplaceCommitAsync("TerraScale/widgets", "old", [(new CodeChunk("old.cs", 1, 1, "Old", "old implementation"), new float[] { 1, 0, 0, 0, 0, 0, 0, 0 })], CancellationToken.None);
            await store.ReplaceCommitAsync("TerraScale/widgets", "new", [(new CodeChunk("new.cs", 1, 1, "New", "new implementation"), new float[] { 0, 1, 0, 0, 0, 0, 0, 0 })], CancellationToken.None);
            var oldResults = await store.SearchAsync("TerraScale/widgets", "old", "implementation", 5, null, CancellationToken.None);
            var newResults = await store.SearchAsync("TerraScale/widgets", "new", "implementation", 5, null, CancellationToken.None);
            await Assert.That(oldResults.Count).IsEqualTo(1);
            await Assert.That(oldResults[0].Path).IsEqualTo("old.cs");
            await Assert.That(newResults.Count).IsEqualTo(1);
            await Assert.That(newResults[0].Path).IsEqualTo("new.cs");
        }
        finally { File.Delete(database); }
    }

    [Test]
    public async Task SqliteContextReplacementRemovesOldChunks()
    {
        var database = Path.Combine(Path.GetTempPath(), "ts-reviewer-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var options = new ReviewerOptions("http://+:8080", "https://forgejo.test", "forgejo", "https://proxy.test/v1", "proxy", "review", "embed", "TerraScale", "bot", 2, TimeSpan.FromMinutes(1), 100000, "", "", database, 100, 8);
            var store = new ContextStore(options);
            await store.ReplaceCommitAsync("TerraScale/widgets", "same", [(new CodeChunk("before.cs", 1, 1, "Before", "before marker"), new float[] { 1, 0, 0, 0, 0, 0, 0, 0 })], CancellationToken.None);
            await store.ReplaceCommitAsync("TerraScale/widgets", "same", [(new CodeChunk("after.cs", 1, 1, "After", "after marker"), new float[] { 1, 0, 0, 0, 0, 0, 0, 0 })], CancellationToken.None);
            var results = await store.SearchAsync("TerraScale/widgets", "same", "marker", 5, null, CancellationToken.None);
            await Assert.That(results.Count).IsEqualTo(1);
            await Assert.That(results[0].Path).IsEqualTo("after.cs");
        }
        finally { File.Delete(database); }
    }

    [Test]
    public async Task ContextIndexerFetchesExactCommitAndEmbedsChunks()
    {
        var database = Path.Combine(Path.GetTempPath(), "ts-reviewer-" + Guid.NewGuid().ToString("N") + ".db");
        var options = new ReviewerOptions("http://+:8080", "https://forgejo.test", "forgejo", "https://proxy.test/v1", "proxy", "review", "embed", "TerraScale", "bot", 2, TimeSpan.FromMinutes(1), 100000, "", "", database, 100, 8);
        var handler = new ContextFakeHandler();
        var forgejo = new ForgejoClient(new HttpClient(handler), options);
        var embeddings = new ContextEmbeddingClient(new HttpClient(handler), options);
        var store = new ContextStore(options);
        var indexer = new ContextIndexer(forgejo, store, embeddings, options, NullLogger<ContextIndexer>.Instance);
        try
        {
            await indexer.StartAsync(CancellationToken.None);
            await indexer.EnsureIndexedAsync("TerraScale", "widgets", "abc123", CancellationToken.None);
            var results = await store.SearchAsync("TerraScale/widgets", "abc123", "CreateWidget", 5, null, CancellationToken.None);
            await Assert.That(results.Count).IsEqualTo(1);
            await Assert.That(results[0].Path).IsEqualTo("src/Widget.cs");
            await Assert.That(handler.FileRequests).IsEqualTo(1);
            await indexer.StopAsync(CancellationToken.None);
        }
        finally { File.Delete(database); }
    }

    private sealed class ContextFakeHandler : HttpMessageHandler
    {
        public int FileRequests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath.Contains("/git/trees/", StringComparison.Ordinal) == true)
                return Json(new { tree = new[] { new { path = "src/Widget.cs", type = "blob", size = 58, sha = "file-sha" } } });
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath.Contains("/contents/", StringComparison.Ordinal) == true)
            {
                FileRequests++;
                var content = Convert.ToBase64String(Encoding.UTF8.GetBytes("public class Widget { public Widget CreateWidget() => new(); }"));
                return Json(new { content, encoding = "base64", type = "file" });
            }
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath.EndsWith("/embeddings", StringComparison.Ordinal) == true)
            {
                var count = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.GetProperty("input").GetArrayLength();
                var data = Enumerable.Range(0, count).Select(index => new { index, embedding = new[] { 1f, 0f, 0f, 0f, 0f, 0f, 0f, 0f } });
                return Json(new { data });
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }
}
