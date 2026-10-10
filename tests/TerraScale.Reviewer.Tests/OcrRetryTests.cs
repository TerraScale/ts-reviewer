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
    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task IncompleteReviewsThrowEvenWithPartialFindings(bool findings, bool reportsComplete)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ScriptAsync(Report("Provider failed", "sess-1", findings
            ? [new { path = "a.cs", content = "A finding", start_line = 1, severity = "high" }] : [], status: reportsComplete ? "complete" : "partial", failedPath: "b.cs"));
        string? failure = null;
        try { await fixture.Engine().RunAsync(fixture.PullRequest, CancellationToken.None); }
        catch (InvalidOperationException ex) { failure = ex.Message; }
        await Assert.That(failure).IsEqualTo("ocr review failed: Provider failed");
        await Assert.That((await fixture.RunsAsync()).Length).IsEqualTo(1);
    }

    [Test]
    public async Task OcrRunsOfflineOnAPartialCloneWithEverythingItReadsDownloaded()
    {
        await using var fixture = await Fixture.CreateAsync(servePartialClones: true);
        await fixture.ScriptAsync(
            "fail() { echo \"Error: $1\" >&2; exit 3; }\n" +
            "from=$(printf '%s' \"$args\" | sed -n 's/.*--from \\([0-9a-f]*\\).*/\\1/p')\n" +
            "to=$(printf '%s' \"$args\" | sed -n 's/.*--to \\([0-9a-f]*\\).*/\\1/p')\n" +
            "test \"$(git config remote.origin.promisor)\" = true || fail 'not a partial clone'\n" +
            "test \"$GIT_NO_LAZY_FETCH\" = 1 || fail 'ocr may fetch lazily'\n" +
            // ocr's diff, its merge-base side, and a head file must all be local.
            "mb=$(git merge-base \"$from\" \"$to\") || fail 'no merge-base'\n" +
            "git diff --find-renames \"$mb\" \"$to\" >/dev/null || fail 'diff needs a missing blob'\n" +
            "git show \"$mb:a.cs\" >/dev/null || fail 'merge-base side missing'\n" +
            "git show \"$to:b.cs\" >/dev/null || fail 'head file missing'\n" +
            Report("Review complete: 0 finding(s) across 2 selected item(s).", "sess-1", [], status: "complete"));

        var result = await fixture.Engine().RunAsync(fixture.PullRequest, CancellationToken.None);

        await Assert.That(result.Findings.Count).IsEqualTo(0);
    }

    [Test]
    public async Task FailuresReportTheErrorLineRatherThanTheUsageReport()
    {
        const string usage = "{\n  \"usage\": {\n    \"elapsed\": \"10m1s\"\n  },\n  \"tool_calls\": {\n    \"by_tool\": {\n      \"code_search\": 7\n    }\n  }\n}\n";
        await Assert.That(ReviewEngine.ErrorLine("[ocr] TraceID: t1\n" + usage + "Error: review failed: all 2 file review(s) failed\n"))
            .IsEqualTo("review failed: all 2 file review(s) failed");
        await Assert.That(ReviewEngine.ErrorLine("[ocr] Session: s1 (retry with: --resume s1)\n" + usage))
            .IsEqualTo("[ocr] Session: s1 (retry with: --resume s1)");
        await Assert.That(ReviewEngine.ErrorLine("remote: Counting objects\nfatal: fetch-pack: invalid index-pack output\n"))
            .IsEqualTo("fatal: fetch-pack: invalid index-pack output");
        await Assert.That(ReviewEngine.ErrorLine("")).IsEqualTo("no error output");
    }

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
        private readonly string _forgejoUrl;
        private readonly string _stub;
        private readonly string _runs;

        private Fixture(string root, string forgejoUrl, PullRequest pullRequest)
        {
            (_root, _forgejoUrl, PullRequest) = (root, forgejoUrl, pullRequest);
            _stub = Path.Combine(root, "ocr");
            _runs = Path.Combine(root, "runs.log");
        }

        public PullRequest PullRequest { get; }

        /// <summary>
        /// A local remote with a two-commit pull request. Plain paths use git's
        /// local transport, which ignores fetch filters; <paramref name="servePartialClones"/>
        /// serves the remote over file:// with filters enabled, like Forgejo.
        /// </summary>
        public static async Task<Fixture> CreateAsync(bool servePartialClones = false)
        {
            if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) throw new TUnit.Core.Exceptions.SkipTestException("requires a POSIX host with git");
            var root = Path.Combine(Path.GetTempPath(), "ts-reviewer-ocr-" + Guid.NewGuid().ToString("N"));
            var remote = Path.Combine(root, "TerraScale", "widgets.git");
            var work = Path.Combine(root, "work");
            Directory.CreateDirectory(Path.GetDirectoryName(remote)!);
            await GitAsync(root, "init", "--quiet", "-b", "main", "--bare", remote);
            if (servePartialClones)
            {
                await GitAsync(remote, "config", "uploadpack.allowFilter", "true");
                await GitAsync(remote, "config", "uploadpack.allowAnySHA1InWant", "true");
            }
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
            return new Fixture(root, servePartialClones ? "file://" + root : root,
                new PullRequest(9, "Test change", "", new Branch(baseSha, "main", repository), new Branch(headSha, "feature", repository)));
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

        public ReviewEngine Engine() => new(new ReviewerOptions("http://+:8080", _forgejoUrl, "forgejo",
            "https://proxy.test/v1", "proxy", "TerraScale", "bot", TimeSpan.FromMinutes(1),
            "webhook-test-secret", "context-test-token", Path.Combine(_root, "context.db"), 100),
            NullLogger<ReviewEngine>.Instance, _stub);

        public string Runs => _runs;

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
