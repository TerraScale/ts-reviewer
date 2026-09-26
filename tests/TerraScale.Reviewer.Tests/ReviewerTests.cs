using System.Diagnostics;
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
        new("http://+:8080", "https://forgejo.test", "forgejo", "https://proxy.test/v1", "proxy", "review", "TerraScale", "bot", 2, TimeSpan.FromMinutes(1), "webhook-test-secret", "context-test-token", database, 100, 5, null, "operator-test-token");

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
        await Assert.That(WebhookHandler.ParseCommand("please @terrascale-bot escalate this", "terrascale-bot")).IsEqualTo("escalate");
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
        var options = ReviewerOptions.Load(["--forgejo-token=forgejo", "--proxy-token=proxy", "--webhook-secret=webhook-test-secret", "--context-api-token=context-test-token", "--operator-token=operator-test-token", "--forgejo-url=https://forgejo.test/", "--proxy-url=https://proxy.test/v1/", "--timeout=00:00:05", "--max-concurrent=3", "--ocr-concurrency=2"]);
        await Assert.That(options.ForgejoUrl).IsEqualTo("https://forgejo.test");
        await Assert.That(options.ProxyUrl).IsEqualTo("https://proxy.test/v1");
        await Assert.That(options.MaxConcurrent).IsEqualTo(3);
        await Assert.That(options.OcrConcurrency).IsEqualTo(2);
        await Assert.That(options.ContextMaxAttempts).IsEqualTo(5);
        await Assert.That(options.ContextRetryDelay).IsEqualTo(TimeSpan.FromSeconds(1));
        await Assert.That(options.OcrMaxAttempts).IsEqualTo(3);
        await Assert.That(options.OcrRetryDelay).IsEqualTo(TimeSpan.FromSeconds(30));

        var rejected = false;
        try { ReviewerOptions.Load(["--forgejo-token=forgejo", "--proxy-token=proxy", "--webhook-secret=webhook-test-secret", "--context-api-token=context-test-token", "--operator-token=operator-test-token", "--max-concurrent=0"]); }
        catch (InvalidOperationException) { rejected = true; }
        await Assert.That(rejected).IsTrue();
    }

    [Test]
    public async Task ConfigurationDefaultsReviewTimeoutToOneHour()
    {
        const string environmentName = "TS_REVIEWER_TIMEOUT";
        var previous = Environment.GetEnvironmentVariable(environmentName);
        try
        {
            Environment.SetEnvironmentVariable(environmentName, null);
            var options = ReviewerOptions.Load(["--forgejo-token=forgejo", "--proxy-token=proxy",
                "--webhook-secret=webhook-test-secret", "--context-api-token=context-test-token",
                "--operator-token=operator-test-token"]);
            await Assert.That(options.ReviewTimeout).IsEqualTo(TimeSpan.FromHours(1));
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentName, previous);
        }
    }

    [Test]
    public async Task OcrCommentsMapToFindingsAndDropDuplicates()
    {
        var comment = new OcrComment("src/Worker.cs", "The fallback bypasses validation.", "Validate(input);", "return Fallback(input);", 42, 42, "bug", "high");
        var duplicate = comment with { };
        var other = comment with { Content = "Different defect.", StartLine = 7, Severity = "critical" };
        var blank = comment with { Content = " " };
        var result = ReviewEngine.Map([comment, duplicate, other, blank]);
        await Assert.That(result.Findings.Count).IsEqualTo(2);
        var first = result.Findings[0];
        await Assert.That(first.Severity).IsEqualTo("high");
        await Assert.That(first.Path).IsEqualTo("src/Worker.cs");
        await Assert.That(first.Line).IsEqualTo(42);
        await Assert.That(first.Fix.Contains("Validate(input);")).IsTrue();
        var second = result.Findings[1];
        await Assert.That(second.Severity).IsEqualTo("critical");
        await Assert.That(second.Line).IsEqualTo(7);
    }

    [Test]
    public async Task OcrCommentWithoutSeverityDefaultsToMedium()
    {
        var result = ReviewEngine.Map([new OcrComment("a.cs", "content", null, null, 1, 1, "other", null)]);
        await Assert.That(result.Findings.Single().Severity).IsEqualTo("medium");
    }

    [Test]
    public async Task NoItemsSelectedIsRecognizedAsACleanReview()
    {
        await Assert.That(ReviewEngine.IsNoItemsSelected("Review skipped: no items were selected.")).IsTrue();
        await Assert.That(ReviewEngine.IsNoItemsSelected("review failed: provider unavailable")).IsFalse();
        await Assert.That(ReviewEngine.IsNoItemsSelected(null)).IsFalse();
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

        var finding = new Finding("f-1", "high", "Unsafe fallback", "The fallback bypasses validation.", "Add a validation check before retrying.", "src/Worker.cs", 42);
        var formatted = WebhookHandler.Format(pullRequest, new ReviewResult("ignored", [finding]));
        await Assert.That(formatted.Contains("### Findings (1)")).IsTrue();
        await Assert.That(formatted.Contains("#### HIGH · Unsafe fallback")).IsTrue();
        await Assert.That(formatted.Contains("<summary>Suggested fix</summary>")).IsTrue();
        await Assert.That(formatted.Contains("model")).IsFalse();

        var escalated = WebhookHandler.Format(pullRequest, new ReviewResult("ignored", [finding]), "gpt-6-astra");
        await Assert.That(escalated.Contains("model `gpt-6-astra`")).IsTrue();
    }

    [Test]
    [Arguments("high", "REQUEST_CHANGES", "failure")]
    [Arguments("medium", "REQUEST_CHANGES", "failure")]
    [Arguments("low", "REQUEST_CHANGES", "failure")]
    [Arguments(null, "APPROVED", "success")]
    public async Task ReviewEngineRunsOcrOverAClonedPullRequestRange(string? severity, string expectedEvent, string expectedStatus)
    {
        if (!ToolAvailable("git") || !ToolAvailable("chmod")) throw new TUnit.Core.Exceptions.SkipTestException("requires a POSIX host with git and chmod");

        var root = Path.Combine(Path.GetTempPath(), "ts-reviewer-e2e-" + Guid.NewGuid().ToString("N"));
        var stub = Path.Combine(root, "stub");
        var database = Path.Combine(root, "context.db");
        Directory.CreateDirectory(stub);
        var work = Path.Combine(root, "work");
        try
        {
            // A local bare "Forgejo" laid out like the real one: <root>/<owner>/<repo>.git
            var remote = Path.Combine(root, "TerraScale", "widgets.git");
            Directory.CreateDirectory(Path.GetDirectoryName(remote)!);
            await RunGitAsync(root, "init", "--quiet", "-b", "main", "--bare", remote);
            Directory.CreateDirectory(work);
            await RunGitAsync(root, "clone", "--quiet", remote, work);
            await RunGitAsync(work, "config", "user.email", "review@test");
            await RunGitAsync(work, "config", "user.name", "review");
            await File.WriteAllTextAsync(Path.Combine(work, "a.cs"), "class A {}\n");
            await RunGitAsync(work, "add", ".");
            await RunGitAsync(work, "commit", "--quiet", "-m", "base");
            var baseSha = (await RunGitAsync(work, "rev-parse", "HEAD")).Trim();
            await File.WriteAllTextAsync(Path.Combine(work, "a.cs"), "class A { public void B() { } }\n");
            await RunGitAsync(work, "add", ".");
            await RunGitAsync(work, "commit", "--quiet", "-m", "head");
            var headSha = (await RunGitAsync(work, "rev-parse", "HEAD")).Trim();
            await RunGitAsync(work, "push", "--quiet", "origin", baseSha + ":refs/heads/main", headSha + ":refs/pull/9/head");

            // The ocr stub records its arguments AND the OCR_LLM_* endpoint
            // environment the engine must export, then emits a fixed review.
            var log = Path.Combine(stub, "ocr.log");
            var review = Path.Combine(stub, "review.json");
            await File.WriteAllTextAsync(Path.Combine(stub, "ocr"),
                "#!/bin/sh\n" +
                "printf '%s\\n' \"$*\" >> \"" + log + "\"\n" +
                "printenv | grep '^OCR_LLM_\\|^OCR_ENABLE_TELEMETRY=' >> \"" + log + "\"\n" +
                "out=\"\"; while [ $# -gt 0 ]; do case $1 in --output) out=$2 ;; esac; shift; done\n" +
                "cat \"" + review + "\" > \"$out\"\n");
            await File.WriteAllTextAsync(review, JsonSerializer.Serialize(new
            {
                status = "complete",
                message = "Review complete.",
                comments = severity is null ? [] : new object[]
                {
                    new { path = "a.cs", content = "Empty method body hides the failure path.", suggestion_code = "public void B() { throw new NotSupportedException(); }", existing_code = "public void B() { }", start_line = 1, end_line = 1, category = "bug", severity },
                },
            }));
            var expectedFindings = severity is null ? 0 : 1;
            await RunChmodAsync(Path.Combine(stub, "ocr"));

            var options = Options(database) with { ForgejoUrl = root };
            var engine = new ReviewEngine(options, NullLogger<ReviewEngine>.Instance, Path.Combine(stub, "ocr"));
            var repository = new Repository(new Owner("TerraScale"), "widgets");
            var pullRequest = new PullRequest(9, "Test change", "", new Branch(baseSha, "main", repository), new Branch(headSha, "feature", repository));
            var result = await engine.RunAsync(pullRequest, CancellationToken.None);
            await Assert.That(result.Findings.Count).IsEqualTo(expectedFindings);
            if (severity is not null)
            {
                await Assert.That(result.Findings[0].Path).IsEqualTo("a.cs");
                await Assert.That(result.Findings[0].Severity).IsEqualTo(severity);
            }
            var recorded = await File.ReadAllTextAsync(log);
            await Assert.That(recorded.Contains("--from " + baseSha)).IsTrue();
            await Assert.That(recorded.Contains("--to " + headSha)).IsTrue();
            await Assert.That(recorded.Contains("--format json")).IsTrue();
            await Assert.That(recorded.Contains("--concurrency 1")).IsTrue();
            await Assert.That(recorded.Contains("OCR_LLM_URL=https://proxy.test/v1")).IsTrue();
            await Assert.That(recorded.Contains("OCR_LLM_TOKEN=proxy")).IsTrue();
            await Assert.That(recorded.Contains("OCR_LLM_MODEL=review")).IsTrue();
            await Assert.That(recorded.Contains("OCR_LLM_PROTOCOL=openai")).IsTrue();
            await Assert.That(recorded.Contains("OCR_LLM_TIMEOUT=180")).IsTrue();
            await Assert.That(recorded.Contains("OCR_ENABLE_TELEMETRY=false")).IsTrue();

            // An escalated review re-runs the same head on the escalation model.
            await engine.RunAsync(pullRequest, CancellationToken.None, "gpt-6-astra");
            recorded = await File.ReadAllTextAsync(log);
            await Assert.That(recorded.Contains("OCR_LLM_MODEL=gpt-6-astra")).IsTrue();

            // Exercise the real webhook-to-HTTP publication path. Forgejo uses
            // APPROVED, not GitHub's APPROVE; the latter creates a pending draft.
            var apiOptions = Options(database);
            var transport = new ReviewPublicationHandler(pullRequest);
            using var http = new HttpClient(transport);
            var forgejo = new ForgejoClient(http, apiOptions);
            using var store = new ContextStore(apiOptions);
            var indexer = new ContextIndexer(forgejo, store, Embedder, apiOptions, NullLogger<ContextIndexer>.Instance);
            var webhook = new WebhookHandler(forgejo, engine, indexer, apiOptions, NullLogger<WebhookHandler>.Instance);
            await PostWebhookAsync(webhook, new
            {
                action = "created",
                number = 9,
                repository = new { name = "widgets", owner = new { login = "TerraScale" } },
                comment = new { id = 77, body = "@bot recheck" },
            });
            var pendingStatus = await transport.PendingStatus.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var publishedStatus = await transport.Status.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var publishedReview = await transport.Review.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await Assert.That(pendingStatus.GetProperty("state").GetString()).IsEqualTo("pending");
            await Assert.That(publishedReview.GetProperty("event").GetString()).IsEqualTo(expectedEvent);
            await Assert.That(publishedReview.GetProperty("commit_id").GetString()).IsEqualTo(headSha);
            await Assert.That(publishedReview.GetProperty("comments").GetArrayLength()).IsEqualTo(expectedFindings);
            await Assert.That(publishedStatus.GetProperty("state").GetString()).IsEqualTo(expectedStatus);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Test]
    public async Task ReviewEngineRetriesFailedOcrRuns()
    {
        if (!ToolAvailable("git") || !ToolAvailable("chmod")) throw new TUnit.Core.Exceptions.SkipTestException("requires a POSIX host with git and chmod");

        var root = Path.Combine(Path.GetTempPath(), "ts-reviewer-retry-" + Guid.NewGuid().ToString("N"));
        var stub = Path.Combine(root, "stub");
        Directory.CreateDirectory(stub);
        var work = Path.Combine(root, "work");
        try
        {
            var remote = Path.Combine(root, "TerraScale", "widgets.git");
            Directory.CreateDirectory(Path.GetDirectoryName(remote)!);
            await RunGitAsync(root, "init", "--quiet", "-b", "main", "--bare", remote);
            Directory.CreateDirectory(work);
            await RunGitAsync(root, "clone", "--quiet", remote, work);
            await RunGitAsync(work, "config", "user.email", "review@test");
            await RunGitAsync(work, "config", "user.name", "review");
            await File.WriteAllTextAsync(Path.Combine(work, "a.cs"), "class A {}\n");
            await RunGitAsync(work, "add", ".");
            await RunGitAsync(work, "commit", "--quiet", "-m", "base");
            var baseSha = (await RunGitAsync(work, "rev-parse", "HEAD")).Trim();
            await File.WriteAllTextAsync(Path.Combine(work, "a.cs"), "class A { public void B() { } }\n");
            await RunGitAsync(work, "add", ".");
            await RunGitAsync(work, "commit", "--quiet", "-m", "head");
            var headSha = (await RunGitAsync(work, "rev-parse", "HEAD")).Trim();
            await RunGitAsync(work, "push", "--quiet", "origin", baseSha + ":refs/heads/main", headSha + ":refs/pull/9/head");

            // The first run fails like an exhausted LLM endpoint, the second
            // reports a partial review with no findings, and the third succeeds.
            var attempts = Path.Combine(stub, "attempts");
            await File.WriteAllTextAsync(Path.Combine(stub, "ocr"),
                "#!/bin/sh\n" +
                "echo run >> \"" + attempts + "\"\n" +
                "count=$(wc -l < \"" + attempts + "\")\n" +
                "out=\"\"; while [ $# -gt 0 ]; do case $1 in --output) out=$2 ;; esac; shift; done\n" +
                "if [ \"$count\" -eq 1 ]; then echo 'all 1 file review(s) failed' >&2; exit 1; fi\n" +
                "if [ \"$count\" -eq 2 ]; then echo '{\"status\":\"partial\",\"message\":\"0 of 1 selected items reviewed\",\"comments\":[]}' > \"$out\"; exit 0; fi\n" +
                "echo '{\"status\":\"complete\",\"message\":\"Review complete.\",\"comments\":[]}' > \"$out\"\n");
            await RunChmodAsync(Path.Combine(stub, "ocr"));

            var options = Options(Path.Combine(root, "context.db")) with { ForgejoUrl = root, OcrRetryBaseDelay = TimeSpan.FromMilliseconds(10) };
            var engine = new ReviewEngine(options, NullLogger<ReviewEngine>.Instance, Path.Combine(stub, "ocr"));
            var repository = new Repository(new Owner("TerraScale"), "widgets");
            var pullRequest = new PullRequest(9, "Test change", "", new Branch(baseSha, "main", repository), new Branch(headSha, "feature", repository));
            var result = await engine.RunAsync(pullRequest, CancellationToken.None);
            await Assert.That(result.Findings.Count).IsEqualTo(0);
            await Assert.That((await File.ReadAllLinesAsync(attempts)).Length).IsEqualTo(3);

            // Exhausting every attempt surfaces the last failure.
            await File.WriteAllTextAsync(attempts, string.Empty);
            var limited = new ReviewEngine(options with { OcrMaxAttempts = 1 }, NullLogger<ReviewEngine>.Instance, Path.Combine(stub, "ocr"));
            var failed = false;
            try { await limited.RunAsync(pullRequest, CancellationToken.None); }
            catch (InvalidOperationException) { failed = true; }
            await Assert.That(failed).IsTrue();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Test]
    public async Task ReviewEngineKillsOcrAndPropagatesCancellationWhenTheDeadlinePasses()
    {
        if (!ToolAvailable("git") || !ToolAvailable("chmod")) throw new TUnit.Core.Exceptions.SkipTestException("requires a POSIX host with git and chmod");

        var root = Path.Combine(Path.GetTempPath(), "ts-reviewer-cancel-" + Guid.NewGuid().ToString("N"));
        var stub = Path.Combine(root, "stub");
        Directory.CreateDirectory(stub);
        var work = Path.Combine(root, "work");
        try
        {
            var remote = Path.Combine(root, "TerraScale", "widgets.git");
            Directory.CreateDirectory(Path.GetDirectoryName(remote)!);
            await RunGitAsync(root, "init", "--quiet", "-b", "main", "--bare", remote);
            Directory.CreateDirectory(work);
            await RunGitAsync(root, "clone", "--quiet", remote, work);
            await RunGitAsync(work, "config", "user.email", "review@test");
            await RunGitAsync(work, "config", "user.name", "review");
            await File.WriteAllTextAsync(Path.Combine(work, "a.cs"), "class A {}\n");
            await RunGitAsync(work, "add", ".");
            await RunGitAsync(work, "commit", "--quiet", "-m", "base");
            var baseSha = (await RunGitAsync(work, "rev-parse", "HEAD")).Trim();
            await File.WriteAllTextAsync(Path.Combine(work, "a.cs"), "class A { public void B() { } }\n");
            await RunGitAsync(work, "add", ".");
            await RunGitAsync(work, "commit", "--quiet", "-m", "head");
            var headSha = (await RunGitAsync(work, "rev-parse", "HEAD")).Trim();
            await RunGitAsync(work, "push", "--quiet", "origin", baseSha + ":refs/heads/main", headSha + ":refs/pull/9/head");

            // The ocr stub blocks well past the deadline so the review cancels mid-run.
            await File.WriteAllTextAsync(Path.Combine(stub, "ocr"), "#!/bin/sh\nsleep 60\n");
            await RunChmodAsync(Path.Combine(stub, "ocr"));

            var options = Options(Path.Combine(root, "context.db")) with { ForgejoUrl = root };
            var engine = new ReviewEngine(options, NullLogger<ReviewEngine>.Instance, Path.Combine(stub, "ocr"));
            var repository = new Repository(new Owner("TerraScale"), "widgets");
            var pullRequest = new PullRequest(9, "Test change", "", new Branch(baseSha, "main", repository), new Branch(headSha, "feature", repository));
            var canceled = false;
            try { using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await engine.RunAsync(pullRequest, cts.Token); }
            catch (OperationCanceledException) { canceled = true; }
            await Assert.That(canceled).IsTrue();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static bool ToolAvailable(string name)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate)) return true;
        }
        return false;
    }

    private static async Task<string> RunGitAsync(string working, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = working,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException($"git {arguments.FirstOrDefault()} failed: {error}");
        return output;
    }

    private static async Task RunChmodAsync(string path)
    {
        using var process = Process.Start(new ProcessStartInfo { FileName = "chmod", Arguments = "+x " + path, UseShellExecute = false }) ?? throw new InvalidOperationException("chmod missing");
        await process.WaitForExitAsync();
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
    public async Task LiteDbSupportsConcurrentProcessConnections()
    {
        var database = Path.Combine(Path.GetTempPath(), "ts-reviewer-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var first = new ContextStore(Options(database));
            using var second = new ContextStore(Options(database));
            first.EnqueueIndexJob("TerraScale", "widgets", "shared-a");
            second.EnqueueIndexJob("TerraScale", "widgets", "shared-b");
            await Assert.That(first.PendingIndexJobs()).Count().IsEqualTo(2);
            await Assert.That(second.PendingIndexJobs()).Count().IsEqualTo(2);
        }
        finally { File.Delete(database); }
    }

    [Test]
    public async Task FailedIndexJobsPersistBackoffAndDeadLetterState()
    {
        var database = Path.Combine(Path.GetTempPath(), "ts-reviewer-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var store = new ContextStore(Options(database));
            store.EnqueueIndexJob("TerraScale", "widgets", "failing");
            var first = store.RecordIndexFailure("TerraScale/widgets", "failing", "upstream unavailable", 3, TimeSpan.FromMilliseconds(10));
            await Assert.That(first.DeadLettered).IsFalse();
            var status = store.GetIndexJobStatus("TerraScale/widgets", "failing");
            await Assert.That(status?.Attempts).IsEqualTo(1);
            await Assert.That(status?.LastError).IsEqualTo("upstream unavailable");
            await Assert.That(status?.NextAttemptAt > 0).IsTrue();

            store.RecordIndexFailure("TerraScale/widgets", "failing", "still unavailable", 3, TimeSpan.FromMilliseconds(10));
            var final = store.RecordIndexFailure("TerraScale/widgets", "failing", "permanent failure", 3, TimeSpan.FromMilliseconds(10));
            await Assert.That(final.DeadLettered).IsTrue();
            await Assert.That(store.GetIndexJobStatus("TerraScale/widgets", "failing")?.DeadLettered).IsTrue();
            await Assert.That(store.PendingIndexJobs()).IsEmpty();
            await Assert.That(store.DeadLetterJobs()).Count().IsEqualTo(1);
            await Assert.That(store.DeadLetterJobs()[0].LastError).IsEqualTo("permanent failure");
            await Assert.That(store.ReplayDeadLetter("TerraScale/widgets", "failing")).IsTrue();
            await Assert.That(store.GetIndexJobStatus("TerraScale/widgets", "failing")?.DeadLettered).IsFalse();
            await Assert.That(store.DeadLetterJobs()).IsEmpty();
        }
        finally { File.Delete(database); }
    }

    [Test]
    public async Task IndexJobLeaseFencesCompetingWorkers()
    {
        var database = Path.Combine(Path.GetTempPath(), "ts-reviewer-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var first = new ContextStore(Options(database));
            using var second = new ContextStore(Options(database));
            first.EnqueueIndexJob("TerraScale", "widgets", "leased");
            var token = first.TryClaimIndexJob("TerraScale/widgets", "leased", "worker-a", TimeSpan.FromMinutes(1));
            await Assert.That(token).IsNotNull();
            await Assert.That(second.TryClaimIndexJob("TerraScale/widgets", "leased", "worker-b", TimeSpan.FromMinutes(1))).IsNull();
            first.ReleaseIndexJob("TerraScale/widgets", "leased", "worker-a", "wrong-token");
            await Assert.That(second.TryClaimIndexJob("TerraScale/widgets", "leased", "worker-b", TimeSpan.FromMinutes(1))).IsNull();
            first.ReleaseIndexJob("TerraScale/widgets", "leased", "worker-a", token!);
            await Assert.That(second.TryClaimIndexJob("TerraScale/widgets", "leased", "worker-b", TimeSpan.FromMinutes(1))).IsNotNull();
        }
        finally { File.Delete(database); }
    }

    [Test]
    public async Task IndexJobLeaseReclaimsAfterOwnerDisappears()
    {
        var database = Path.Combine(Path.GetTempPath(), "ts-reviewer-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            string token;
            using (var first = new ContextStore(Options(database)))
            {
                first.EnqueueIndexJob("TerraScale", "widgets", "crashed");
                token = first.TryClaimIndexJob("TerraScale/widgets", "crashed", "worker-crashed", TimeSpan.FromMilliseconds(25))!;
                await Assert.That(token).IsNotNull();
            }

            await Task.Delay(50);

            using var replacement = new ContextStore(Options(database));
            var replacementToken = replacement.TryClaimIndexJob(
                "TerraScale/widgets", "crashed", "worker-replacement", TimeSpan.FromMinutes(1));
            await Assert.That(replacementToken).IsNotNull();
            await Assert.That(replacementToken).IsNotEqualTo(token);
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
            await Assert.That(indexer.Enqueue("TerraScale", "widgets", "abc123")).IsTrue();
            await Assert.That(indexer.Enqueue("TerraScale", "widgets", "abc123")).IsTrue();
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
    public async Task ContextIndexJobsSurviveIndexerRestart()
    {
        var database = Path.Combine(Path.GetTempPath(), "ts-reviewer-" + Guid.NewGuid().ToString("N") + ".db");
        var options = Options(database);
        var handler = new ContextFakeHandler();
        try
        {
            using (var firstStore = new ContextStore(options))
            {
                var firstIndexer = new ContextIndexer(new ForgejoClient(new HttpClient(handler), options), firstStore,
                    Embedder, options, NullLogger<ContextIndexer>.Instance);
                await Assert.That(firstIndexer.Enqueue("TerraScale", "widgets", "restart-commit")).IsTrue();
                // The worker never starts: the persisted job is the recovery source.
            }

            using var recoveredStore = new ContextStore(options);
            var recovered = new ContextIndexer(new ForgejoClient(new HttpClient(handler), options), recoveredStore,
                Embedder, options, NullLogger<ContextIndexer>.Instance);
            await recovered.StartAsync(CancellationToken.None);
            await recovered.EnsureIndexedAsync("TerraScale", "widgets", "restart-commit", CancellationToken.None);
            await Assert.That(handler.FileRequests).IsEqualTo(1);
            await recovered.StopAsync(CancellationToken.None);
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
        var engine = new ReviewEngine(options, NullLogger<ReviewEngine>.Instance);
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
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.Headers["X-Forgejo-Signature"] = Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes("webhook-test-secret"), bytes));
        return await handler.HandleAsync(context.Request, CancellationToken.None);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!condition() && !timeout.Token.IsCancellationRequested) await Task.Delay(25);
        await Assert.That(condition()).IsTrue();
    }

    private sealed class ReviewPublicationHandler(PullRequest pullRequest) : HttpMessageHandler
    {
        public TaskCompletionSource<JsonElement> Review { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<JsonElement> PendingStatus { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<JsonElement> Status { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get)
            {
                if (path.EndsWith("/pulls/9", StringComparison.Ordinal))
                    return new(HttpStatusCode.OK) { Content = JsonContent.Create(pullRequest) };
                if (path.EndsWith("/pulls/9.diff", StringComparison.Ordinal))
                    return new(HttpStatusCode.OK) { Content = new StringContent("diff --git a/a.cs b/a.cs\n@@ -1 +1 @@\n-class A {}\n+class A { public void B() { } }\n") };
                if (path.EndsWith("/files", StringComparison.Ordinal) || path.EndsWith("/comments", StringComparison.Ordinal))
                    return new(HttpStatusCode.OK) { Content = new StringContent("[]", Encoding.UTF8, "application/json") };
            }
            else if (request.Method == HttpMethod.Post)
            {
                if (path.EndsWith("/reviews", StringComparison.Ordinal))
                    Review.TrySetResult(await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken));
                if (path.EndsWith("/statuses/" + pullRequest.Head.Sha, StringComparison.Ordinal))
                {
                    var status = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
                    if (status.GetProperty("state").GetString() == "pending") PendingStatus.TrySetResult(status);
                    else Status.TrySetResult(status);
                }
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { }) };
            }
            return new(HttpStatusCode.NotFound);
        }
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

    [Test]
    public async Task IndexJobLeaseReclaimsAfterChildProcessIsKilled()
    {
        var database = Path.Combine(Path.GetTempPath(), "ts-reviewer-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var setup = new ContextStore(Options(database)))
            {
                setup.EnqueueIndexJob("TerraScale", "widgets", "process-crash");
            }

            var childAssembly = Path.Combine(AppContext.BaseDirectory, "TerraScale.Reviewer.LeaseCrashChild.dll");
            await Assert.That(File.Exists(childAssembly)).IsTrue();
            var childStartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            childStartInfo.ArgumentList.Add(childAssembly);
            childStartInfo.ArgumentList.Add(database);
            using var child = Process.Start(childStartInfo)
                ?? throw new InvalidOperationException("Could not start the lease crash child.");

            var claimed = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(claimed).IsNotNull();
            await Assert.That(claimed).StartsWith("CLAIMED ");

            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(150);

            using var replacement = new ContextStore(Options(database));
            var replacementToken = replacement.TryClaimIndexJob(
                "TerraScale/widgets", "process-crash", "worker-replacement", TimeSpan.FromMinutes(1));
            await Assert.That(replacementToken).IsNotNull();
            await Assert.That(replacementToken).IsNotEqualTo(claimed!["CLAIMED ".Length..]);
        }
        finally
        {
            File.Delete(database);
        }
    }
}
