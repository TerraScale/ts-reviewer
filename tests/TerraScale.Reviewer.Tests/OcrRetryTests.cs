using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TerraScale.Reviewer.Features.Configuration;
using TerraScale.Reviewer.Features.Forgejo;
using TerraScale.Reviewer.Features.Review;
using TUnit.Assertions;
using TUnit.Core;

/// <summary>
/// Drives <see cref="ReviewEngine"/> against a local git remote and a scripted
/// ocr stub whose behaviour depends on how many times it has run.
/// </summary>
public sealed class OcrRetryTests
{
    private static readonly object FindingA = new { path = "a.cs", content = "Empty method body hides the failure path.", start_line = 1, end_line = 1, severity = "high" };
    private static readonly object FindingB = new { path = "b.cs", content = "Unchecked null dereference in the new branch.", start_line = 2, end_line = 2, severity = "medium" };

    [Test]
    public async Task FailedItemsAreRetriedByResumingTheSession()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ScriptAsync(
            // Run 1: one of two items failed; the finding from the other is kept.
            Report(Partial(1, 1, 2), "sess-1", [FindingA], failedPath: "b.cs"),
            // Run 2 must resume run 1 and completes the missing item.
            "case \"$args\" in *'--resume sess-1'*) ;; *) echo 'expected --resume sess-1' >&2; exit 2 ;; esac\n" +
            Report("Review complete: 2 finding(s) across 2 selected item(s).", "sess-2", [FindingA, FindingB], status: "complete"));

        var result = await fixture.Engine(attempts: 3).RunAsync(fixture.PullRequest, CancellationToken.None);

        await Assert.That(result.Findings.Count).IsEqualTo(2);
        await Assert.That(result.Summary.Contains("partial", StringComparison.OrdinalIgnoreCase)).IsFalse();
        var runs = await fixture.RunsAsync();
        await Assert.That(runs.Length).IsEqualTo(2);
        await Assert.That(runs[0].Contains("--resume")).IsFalse();
        await Assert.That(runs[1].Contains("--resume sess-1")).IsTrue();
    }

    [Test]
    public async Task AFailedRunIsResumedAndARefusedResumeStartsOver()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ScriptAsync(
            // Run 1: every item failed. ocr still writes its report, then exits non-zero.
            Report("Review failed: 0 finding(s); 2 of 2 selected item(s) failed.", "sess-1", [], status: "failed") +
            "echo 'review failed: all 2 file review(s) failed' >&2\necho '[ocr] Session: sess-1 (retry with: --resume sess-1)' >&2\nexit 1\n",
            // Run 2: ocr refuses the resume before reviewing anything.
            "echo 'load resume session: open sess-1.jsonl: no such file or directory' >&2\nexit 1\n",
            // Run 3 must start over without --resume.
            "case \"$args\" in *--resume*) echo 'unexpected resume' >&2; exit 2 ;; esac\n" +
            Report("Review complete: 0 finding(s) across 2 selected item(s).", "sess-3", [], status: "complete"));

        var result = await fixture.Engine(attempts: 3).RunAsync(fixture.PullRequest, CancellationToken.None);

        await Assert.That(result.Findings.Count).IsEqualTo(0);
        var runs = await fixture.RunsAsync();
        await Assert.That(runs.Length).IsEqualTo(3);
        await Assert.That(runs[1].Contains("--resume sess-1")).IsTrue();
        await Assert.That(runs[2].Contains("--resume")).IsFalse();
    }

    [Test]
    public async Task ExhaustedAttemptsPublishPartialFindingsButNeverAPartialPass()
    {
        await using var withFindings = await Fixture.CreateAsync();
        await withFindings.ScriptAsync(Report(Partial(1, 1, 2), "sess-1", [FindingA], failedPath: "b.cs"));
        var partial = await withFindings.Engine(attempts: 2).RunAsync(withFindings.PullRequest, CancellationToken.None);
        await Assert.That(partial.Findings.Count).IsEqualTo(1);
        await Assert.That(partial.Summary.Contains("partial review", StringComparison.Ordinal)).IsTrue();
        await Assert.That((await withFindings.RunsAsync()).Length).IsEqualTo(2);

        await using var clean = await Fixture.CreateAsync();
        await clean.ScriptAsync(Report(Partial(0, 1, 2), "sess-1", [], failedPath: "b.cs"));
        string? failure = null;
        try { await clean.Engine(attempts: 2).RunAsync(clean.PullRequest, CancellationToken.None); }
        catch (InvalidOperationException ex) { failure = ex.Message; }
        await Assert.That(failure).IsEqualTo("ocr review failed: " + Partial(0, 1, 2));
    }

    private static string Partial(int findings, int failed, int selected) =>
        $"Review partially complete: {findings} finding(s); {failed} of {selected} selected item(s) failed.";

    /// <summary>Shell that writes an ocr JSON report to the <c>--output</c> path.</summary>
    private static string Report(string message, string session, object[] comments, string status = "partial", string? failedPath = null)
    {
        var json = JsonSerializer.Serialize(new
        {
            status,
            message,
            session_id = session,
            comments,
            manifest = new { coverage = new { failed = failedPath is null ? [] : new[] { new { path = failedPath, classification = "provider", reason = "provider or subtask request failed" } } } },
        });
        return "cat > \"$out\" <<'JSON'\n" + json + "\nJSON\n";
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly string _stub;
        private readonly string _runs;

        private Fixture(string root, PullRequest pullRequest)
        {
            (_root, PullRequest) = (root, pullRequest);
            _stub = Path.Combine(root, "ocr");
            _runs = Path.Combine(root, "runs.log");
        }

        public PullRequest PullRequest { get; }

        public static async Task<Fixture> CreateAsync()
        {
            if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) throw new TUnit.Core.Exceptions.SkipTestException("requires a POSIX host with git");
            var root = Path.Combine(Path.GetTempPath(), "ts-reviewer-ocr-" + Guid.NewGuid().ToString("N"));
            var remote = Path.Combine(root, "TerraScale", "widgets.git");
            var work = Path.Combine(root, "work");
            Directory.CreateDirectory(Path.GetDirectoryName(remote)!);
            await GitAsync(root, "init", "--quiet", "-b", "main", "--bare", remote);
            await GitAsync(root, "clone", "--quiet", remote, work);
            await GitAsync(work, "config", "user.email", "review@test");
            await GitAsync(work, "config", "user.name", "review");
            await File.WriteAllTextAsync(Path.Combine(work, "a.cs"), "class A {}\n");
            await GitAsync(work, "add", ".");
            await GitAsync(work, "commit", "--quiet", "-m", "base");
            var baseSha = (await GitAsync(work, "rev-parse", "HEAD")).Trim();
            await File.WriteAllTextAsync(Path.Combine(work, "a.cs"), "class A { public void B() { } }\n");
            await File.WriteAllTextAsync(Path.Combine(work, "b.cs"), "class B {}\n");
            await GitAsync(work, "add", ".");
            await GitAsync(work, "commit", "--quiet", "-m", "head");
            var headSha = (await GitAsync(work, "rev-parse", "HEAD")).Trim();
            await GitAsync(work, "push", "--quiet", "origin", baseSha + ":refs/heads/main", headSha + ":refs/pull/9/head");
            var repository = new Repository(new Owner("TerraScale"), "widgets");
            return new Fixture(root, new PullRequest(9, "Test change", "", new Branch(baseSha, "main", repository), new Branch(headSha, "feature", repository)));
        }

        /// <summary>Installs an ocr stub that runs the n-th script on its n-th run and the last one after that.</summary>
        public async Task ScriptAsync(params string[] runs)
        {
            var script = new System.Text.StringBuilder("#!/bin/sh\nargs=\"$*\"\nprintf '%s\\n' \"$args\" >> '" + _runs + "'\n")
                .Append("run=$(wc -l < '" + _runs + "')\n")
                .Append("out=\"\"; while [ $# -gt 0 ]; do case $1 in --output) out=$2 ;; esac; shift; done\n")
                .Append("case $run in\n");
            for (var i = 0; i < runs.Length; i++)
                script.Append(i == runs.Length - 1 ? "*" : (i + 1).ToString()).Append(")\n").Append(runs[i]).Append(";;\n");
            script.Append("esac\n");
            await File.WriteAllTextAsync(_stub, script.ToString());
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_stub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public ReviewEngine Engine(int attempts)
        {
            var options = new ReviewerOptions("http://+:8080", _root, "forgejo", "https://proxy.test/v1", "proxy", "review", "TerraScale", "bot", 1,
                TimeSpan.FromMinutes(1), "webhook-test-secret", "context-test-token", Path.Combine(_root, "context.db"), 100,
                OcrMaxAttempts: attempts, OcrRetryBaseDelay: TimeSpan.FromMilliseconds(10));
            return new ReviewEngine(options, NullLogger<ReviewEngine>.Instance, _stub);
        }

        public async Task<string[]> RunsAsync() => await File.ReadAllLinesAsync(_runs);

        public ValueTask DisposeAsync()
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return ValueTask.CompletedTask;
        }

        private static async Task<string> GitAsync(string working, params string[] arguments)
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = working, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("git missing");
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new InvalidOperationException($"git {arguments[0]} failed: {error}");
            return output;
        }
    }
}
