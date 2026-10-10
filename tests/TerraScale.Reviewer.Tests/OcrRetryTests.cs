using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
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
    public async Task PinnedOcrCompletesReviewThroughLocalProvider()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new TUnit.Core.Exceptions.SkipTestException("requires the production Linux x64 OCR binary");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var binary = await PinnedOcrAsync(deadline.Token);
        await using var fixture = await Fixture.CreateAsync(servePartialClones: true);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        await using var upstream = builder.Build();
        var calls = 0;
        var decodedRead = false;
        var correctRequests = true;
        upstream.MapPost("/v1/messages", async (HttpRequest request) =>
        {
            using var document = await JsonDocument.ParseAsync(request.Body);
            var body = document.RootElement;
            correctRequests &= request.Headers.Authorization == "Bearer proxy"
                && body.GetProperty("model").GetString() == ReviewProvider.PrimaryModel
                && (!body.TryGetProperty("stream", out var stream) || !stream.GetBoolean());
            foreach (var message in body.GetProperty("messages").EnumerateArray())
                if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                    foreach (var block in content.EnumerateArray())
                        if (block.TryGetProperty("type", out var type) && type.GetString() == "tool_result"
                            && block.GetProperty("tool_use_id").GetString() == "read_1")
                            decodedRead |= block.GetRawText().Contains("LINE_RANGE: 1-1", StringComparison.Ordinal)
                                && block.GetRawText().Contains("class A", StringComparison.Ordinal)
                                && (!block.TryGetProperty("is_error", out var error) || !error.GetBoolean());
            var first = ++calls == 1;
            return Results.Json(new
            {
                id = "msg_" + calls,
                type = "message",
                role = "assistant",
                model = ReviewProvider.PrimaryModel,
                stop_reason = "tool_use",
                usage = new { input_tokens = 10, output_tokens = 3 },
                content = new[] { new {
                    type = "tool_use", id = first ? "read_1" : "done_" + calls,
                    name = first ? "file_read" : "task_done",
                    input = JsonNode.Parse(first
                        ? """{"file_path":"a.cs","start_line":1.0,"end_line":1.0}"""
                        : """{"state":"DONE"}""")
                } }
            });
        });
        await upstream.StartAsync(deadline.Token);
        var result = await fixture.Engine(binary, upstream.Urls.Single()).RunAsync(fixture.PullRequest, deadline.Token);
        await Assert.That(result.Findings.Count).IsEqualTo(0);
        await Assert.That(result.Model).IsEqualTo(ReviewProvider.PrimaryModel);
        await Assert.That(calls).IsGreaterThan(1);
        await Assert.That(correctRequests).IsTrue();
        await Assert.That(decodedRead).IsTrue();
    }

    private static async Task<string> PinnedOcrAsync(CancellationToken ct)
    {
        var root = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
        var containerfile = await File.ReadAllLinesAsync(Path.Combine(root, "Containerfile"), ct);
        var version = containerfile.Single(line => line.StartsWith("ARG OCR_VERSION=", StringComparison.Ordinal)).Split('=', 2)[1];
        var digest = containerfile.Single(line => line.StartsWith("ARG OCR_SHA256=", StringComparison.Ordinal)).Split('=', 2)[1];
        var assets = Path.Combine(root, "artifacts", "ci", "ocr");
        Directory.CreateDirectory(assets);
        var archivePath = Path.Combine(assets, "ocr-" + version + ".tgz");
        if (!File.Exists(archivePath))
        {
            using var http = new HttpClient();
            await File.WriteAllBytesAsync(archivePath, await http.GetByteArrayAsync($"https://registry.npmjs.org/@alibaba-group/ocr-linux-x64/-/ocr-linux-x64-{version}.tgz", ct), ct);
        }
        var archive = await File.ReadAllBytesAsync(archivePath, ct);
        await Assert.That(Convert.ToHexStringLower(SHA256.HashData(archive))).IsEqualTo(digest);
        using var compressed = new GZipStream(new MemoryStream(archive), CompressionMode.Decompress);
        using var tar = new TarReader(compressed);
        while (tar.GetNextEntry() is { } entry)
        {
            if (entry.Name != "package/bin/opencodereview") continue;
            var binary = Path.Combine(assets, "ocr");
            await using (var output = File.Create(binary)) await entry.DataStream!.CopyToAsync(output, ct);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(binary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return binary;
        }
        throw new InvalidOperationException("Pinned OCR archive contains no production binary");
    }

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

        public ReviewEngine Engine(string? ocrPath = null, string? proxyUrl = null) => new(new ReviewerOptions("http://+:8080", _forgejoUrl, "forgejo",
            proxyUrl ?? "https://proxy.test/v1", "proxy", "TerraScale", "bot", TimeSpan.FromMinutes(1),
            "webhook-test-secret", "context-test-token", Path.Combine(_root, "context.db"), 100),
            NullLogger<ReviewEngine>.Instance, ocrPath ?? _stub);

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
