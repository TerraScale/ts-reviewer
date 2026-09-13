using TerraScale.Reviewer.Features.Forgejo;
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
}
