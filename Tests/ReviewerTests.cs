using TerraScale.Reviewer.Features.Forgejo;
using TerraScale.Reviewer.Features.Configuration;
using TerraScale.Reviewer.Features.Context;
using TerraScale.Reviewer.Features.Review;
using TerraScale.Reviewer.Features.Webhooks;
using TUnit.Assertions;
using TUnit.Core;

public sealed class ReviewerTests
{
    [Test]
    public async Task SignatureAndCommandValidation()
    {
        await Assert.That(WebhookHandler.ValidSignature("body"u8, "bad", "secret")).IsFalse();
        await Assert.That(WebhookHandler.ParseCommand("please @terrascale-bot recheck", "terrascale-bot")).IsEqualTo("recheck");
        await Assert.That(WebhookHandler.ParseCommand("@other-bot review", "terrascale-bot")).IsNull();
    }

    [Test]
    public async Task UnifiedDiffMapsAddedLines()
    {
        var raw = "diff --git a/main.go b/main.go\n@@ -1,2 +1,3 @@\n package main\n+var value = 1\n func main() {}\n";
        var files = UnifiedDiff.Parse(raw);
        await Assert.That(files.Count).IsEqualTo(1);
        await Assert.That(files[0].Filename).IsEqualTo("main.go");
        await Assert.That(UnifiedDiff.ReviewPosition(files[0].Patch, 2)).IsGreaterThan(0);
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
}
