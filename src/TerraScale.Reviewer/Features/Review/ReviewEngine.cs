using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.Reviewer.Features.Configuration;
using TerraScale.Reviewer.Features.Forgejo;

namespace TerraScale.Reviewer.Features.Review;

/// <summary>
/// Runs reviews through the OpenCodeReview CLI (alibaba/open-code-review).
/// Each review clones the pull request, configures ocr's OpenAI-compatible
/// endpoint through <c>OCR_LLM_*</c> environment variables pointing at the
/// CLIProxy endpoint, invokes <c>ocr review</c> over the exact base..head
/// range, and maps the returned comments to <see cref="Finding"/> records.
/// </summary>
public sealed partial class ReviewEngine(ReviewerOptions options, ILogger<ReviewEngine> logger, string? ocrPath = null)
{
    private readonly string _ocrPath = ocrPath ?? "ocr";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(10);
    private const int FetchAttempts = 3;

    public async Task<ReviewResult> RunAsync(PullRequest pr, CancellationToken ct, string? model = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "ts-reviewer-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "home");
        var repository = Path.Combine(root, "repo");
        var cloneUrl = $"{options.ForgejoUrl}/{pr.Head.Repo.Owner.Login}/{pr.Head.Repo.Name}.git";
        Directory.CreateDirectory(home);
        try
        {
            // Initialize an empty repository and fetch only the two commit tips
            // needed for this review. A full clone can unpack a multi-gigabyte
            // history before ocr starts and exhaust the review container even
            // when only one pull request is active.
            Directory.CreateDirectory(repository);
            await RunGitAsync(home, root, ct, "init", "--quiet", repository);
            await RunGitAsync(home, repository, ct, "remote", "add", "origin", cloneUrl);
            // ocr needs the merge-base between the base and PR tips. A depth-1
            // fetch leaves unrelated tips shallow and makes valid PRs fail
            // with "cannot find merge-base"; keep the two requested refs'
            // ancestry while still avoiding a checked-out working tree.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await RunGitAsync(home, repository, ct, "fetch", "--quiet", "--no-tags", "origin",
                        $"+refs/heads/{pr.Base.Ref}:refs/base", $"+refs/pull/{pr.Number}/head:refs/pr-head");
                    break;
                }
                catch (InvalidOperationException ex) when (attempt < FetchAttempts)
                {
                    logger.LogWarning(ex, "fetch attempt {Attempt} for {CloneUrl} failed; retrying", attempt, cloneUrl);
                    await Task.Delay(TimeSpan.FromSeconds(5 * attempt), ct);
                }
            }
            var head = (await RunGitAsync(home, repository, ct, "rev-parse", "refs/pr-head")).Trim();
            var @base = (await RunGitAsync(home, repository, ct, "rev-parse", "refs/base")).Trim();
            if (!string.Equals(head, pr.Head.Sha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("pull request head moved before the review started");
            await RunGitAsync(home, repository, ct, "checkout", "--quiet", "--detach", head);
            return await ReviewWithRetriesAsync(home, repository, root, @base, head, model, cloneUrl, ct);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Runs ocr until every selected item is reviewed or the attempts run out.
    /// The upstream LLM API is slow and flaky: single file groups fail on
    /// provider errors, timeouts or the model's own tool-call mistakes while the
    /// rest succeed. ocr persists a session in the scratch HOME, so a retry
    /// resumes it (<c>--resume</c>): completed items and their findings are
    /// reused and only the failed items are reviewed again. Retries back off
    /// exponentially; the outer review deadline still bounds every attempt.
    /// </summary>
    private async Task<ReviewResult> ReviewWithRetriesAsync(string home, string repository, string root, string @base, string head, string? model, string cloneUrl, CancellationToken ct)
    {
        string? session = null;
        for (var attempt = 1; ; attempt++)
        {
            var run = await ReviewOnceAsync(home, repository, root, @base, head, model, session, ct);
            if (run.Result is { } result && run.Complete) return result;
            // Resume the session this run reported; a run that produced no
            // session (e.g. a resume ocr refused) starts over from scratch.
            session = run.Session;
            var last = attempt >= options.OcrMaxAttempts;
            logger.LogWarning("ocr attempt {Attempt} of {MaxAttempts} for {CloneUrl} incomplete: {Failure}; {Next}",
                attempt, options.OcrMaxAttempts, cloneUrl, run.Failure, last ? "giving up" : session is null ? "retrying from scratch" : "resuming session " + session);
            if (last)
            {
                // Findings from the reviewed items still request changes; a
                // review without any finding cannot pass on partial coverage.
                if (run.Result is { Findings.Count: > 0 } partial)
                    return partial with { Summary = partial.Summary + $" (partial review: {run.Failure} — re-run for full coverage)" };
                throw new InvalidOperationException(run.Failure);
            }
            await Task.Delay(RetryDelay(options.OcrRetryDelay, attempt), ct);
        }
    }

    private async Task<OcrRun> ReviewOnceAsync(string home, string repository, string root, string @base, string head, string? model, string? session, CancellationToken ct)
    {
        // A fresh output path per attempt so a failed run can never leave a
        // stale review behind for the next attempt to read.
        var output = Path.Combine(root, "review-" + Guid.NewGuid().ToString("N") + ".json");
        // ocr's --timeout bounds each file group in whole minutes, not the
        // whole run. The outer review deadline is the hard backstop.
        List<string> arguments = ["review", "--from", @base, "--to", head, "--concurrency", options.OcrConcurrency.ToString(CultureInfo.InvariantCulture),
            "--timeout", ((int)Math.Ceiling(options.OcrItemTimeout.TotalMinutes)).ToString(CultureInfo.InvariantCulture),
            "--format", "json", "--audience", "agent", "--color", "never", "--output", output];
        if (session is not null) arguments.AddRange(["--resume", session]);
        var process = await RunOcrAsync(home, repository, model, [.. arguments], ct);
        // ocr writes its report even when it exits non-zero (every item
        // failed), so the report and its session take precedence over stderr.
        OcrReview? review = null;
        try { if (File.Exists(output)) review = JsonSerializer.Deserialize<OcrReview>(await File.ReadAllTextAsync(output, ct), JsonOptions); }
        catch (JsonException ex) { logger.LogWarning(ex, "ocr wrote an unreadable review"); }
        var stderrSession = ResumeSession(process.Error);
        if (review is null)
        {
            var failure = process.ExitCode == 0 ? "ocr returned no review" : $"ocr failed with exit code {process.ExitCode}: {Truncate(process.Error)}";
            return new OcrRun(null, false, stderrSession, failure);
        }
        var result = Map(review.Comments ?? []);
        var complete = string.Equals(review.Status, "complete", StringComparison.OrdinalIgnoreCase)
            // OpenCodeReview reports a clean patch with no selected review
            // groups as a skipped review: nothing failed, so it is clean.
            || (result.Findings.Count == 0 && IsNoItemsSelected(review.Message));
        var failed = review.Manifest?.Coverage?.Failed ?? [];
        foreach (var item in failed)
            logger.LogWarning("ocr item {Path} failed ({Classification}): {Reason}", item.Path, item.Classification, item.Reason);
        return new OcrRun(result, complete, review.SessionId ?? stderrSession, $"ocr review failed: {review.Message}");
    }

    /// <summary>The session id from ocr's "retry with: --resume &lt;id&gt;" hint, if it printed one.</summary>
    internal static string? ResumeSession(string stderr) =>
        ResumeHint().Match(stderr) is { Success: true } match ? match.Groups[1].Value : null;

    [GeneratedRegex(@"retry with: --resume ([A-Za-z0-9._-]+)")]
    private static partial Regex ResumeHint();

    /// <summary>Exponential backoff from the base delay, capped at ten minutes per retry.</summary>
    internal static TimeSpan RetryDelay(TimeSpan baseDelay, int attempt) =>
        TimeSpan.FromTicks(Math.Min(baseDelay.Ticks * (1L << Math.Clamp(attempt - 1, 0, 20)), MaxRetryDelay.Ticks));

    internal static ReviewResult Map(IReadOnlyList<OcrComment> comments)
    {
        var findings = new List<Finding>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var comment in comments)
        {
            if (string.IsNullOrWhiteSpace(comment.Path) || string.IsNullOrWhiteSpace(comment.Content)) continue;
            var severity = (comment.Severity ?? string.Empty).Trim().ToLowerInvariant();
            if (severity.Length == 0) severity = "medium";
            var id = comment.Path + ":" + comment.StartLine + ":" + comment.Content.GetHashCode(StringComparison.OrdinalIgnoreCase);
            if (!seen.Add(id)) continue;
            findings.Add(new Finding(id, severity, Title(comment.Content), comment.Content.Trim(), Fix(comment.SuggestionCode), comment.Path, comment.StartLine));
        }
        return new ReviewResult(BuildSummary(findings), findings);
    }

    internal static bool IsNoItemsSelected(string? message) =>
        message?.Contains("no items were selected", StringComparison.OrdinalIgnoreCase) == true;

    private static string Title(string content)
    {
        var text = content.Trim();
        var line = text.Split('\n', 2)[0].Trim();
        if (line.Contains('.'))
        {
            var sentence = line.Split('.', 2)[0];
            if (sentence.Length >= 20) line = sentence.TrimEnd() + ".";
        }
        if (line.Length <= 90) return line;
        // Never split a UTF-16 surrogate pair at the cut.
        var cut = 90;
        if (char.IsHighSurrogate(line[cut - 1])) cut--;
        return line[..cut].TrimEnd() + "…";
    }

    private static string Fix(string? suggestion) =>
        string.IsNullOrWhiteSpace(suggestion) ? string.Empty : $"```\n{suggestion.Trim()}\n```";

    internal static string BuildSummary(IReadOnlyCollection<Finding> findings)
    {
        if (findings.Count == 0) return "The patch passed the review with no actionable findings.";
        var counts = findings
            .GroupBy(finding => finding.Severity.Trim().ToLowerInvariant())
            .OrderBy(group => group.Key)
            .Select(group => $"{group.Count()} {group.Key}");
        return $"Found {findings.Count} actionable finding(s): {string.Join(", ", counts)}.";
    }

    private async Task<string> RunGitAsync(string home, string working, CancellationToken ct, params string[] arguments)
    {
        var result = await RunProcessAsync("git", working, environment =>
        {
            // The Forgejo token rides in git's environment config, never in
            // argv or on disk, so clones and fetches authenticate without
            // leaking the credential to the process list or .git/config.
            // HOME points at the empty per-review scratch dir, so no global
            // git config (e.g. a planted /tmp/.gitconfig) is ever read.
            environment["HOME"] = home;
            environment["GIT_TERMINAL_PROMPT"] = "0";
            environment["GIT_CONFIG_COUNT"] = "1";
            environment["GIT_CONFIG_KEY_0"] = "http.extraHeader";
            environment["GIT_CONFIG_VALUE_0"] = "Authorization: token " + options.ForgejoToken;
        }, ct, arguments);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"git failed with exit code {result.ExitCode}: {Truncate(result.Error)}");
        return result.Output;
    }

    private Task<ProcessResult> RunOcrAsync(string home, string repository, string? model, string[] arguments, CancellationToken ct) =>
        RunProcessAsync(_ocrPath, repository, environment =>
        {
            // ocr's env-only endpoint configuration (OCR_LLM_*): with no
            // ~/.opencodereview/config.json in the scratch HOME, these
            // variables point it at the CLIProxy OpenAI-compatible endpoint
            // and the proxy token never lands on disk. A command-triggered
            // escalation swaps the model on the same endpoint. The scratch
            // HOME also holds ocr's session store, which --resume reads.
            environment["HOME"] = home;
            environment["OCR_LLM_URL"] = options.ProxyUrl;
            environment["OCR_LLM_TOKEN"] = options.ProxyToken;
            environment["OCR_LLM_MODEL"] = model ?? options.Model;
            environment["OCR_LLM_PROTOCOL"] = "openai";
            // Per LLM request, in seconds. The upstream zai GLM API is slow
            // and flaky, so the default is generous; ocr's SDK retries each
            // request five more times on top of the proxy's own retries.
            environment["OCR_LLM_TIMEOUT"] = ((int)options.OcrLlmTimeout.TotalSeconds).ToString(CultureInfo.InvariantCulture);
            environment["OCR_ENABLE_TELEMETRY"] = "false";
        }, ct, arguments);

    private static async Task<ProcessResult> RunProcessAsync(string fileName, string workingDirectory, Action<IDictionary<string, string?>> configureEnvironment, CancellationToken ct, string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        configureEnvironment(process.StartInfo.Environment);
        // Registered before Start so a cancellation that lands between the two
        // cannot leak a running child; killing a not-yet-started process is
        // already swallowed by the InvalidOperationException catch.
        using var registration = ct.Register(() => { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (Win32Exception) { } });
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        await process.WaitForExitAsync(ct);
        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);

    /// <summary>One ocr run: its mapped result when it wrote a report, whether it covered every item, and the session to resume.</summary>
    private sealed record OcrRun(ReviewResult? Result, bool Complete, string? Session, string Failure);

    private static string Truncate(string value) => value.Length <= 500 ? value : value[^500..];
}

public sealed record OcrComment(
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("suggestion_code")] string? SuggestionCode,
    [property: JsonPropertyName("existing_code")] string? ExistingCode,
    [property: JsonPropertyName("start_line")] int StartLine,
    [property: JsonPropertyName("end_line")] int EndLine,
    [property: JsonPropertyName("category")] string? Category,
    [property: JsonPropertyName("severity")] string? Severity);

public sealed record OcrReview
{
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("comments")]
    public IReadOnlyList<OcrComment>? Comments { get; init; }

    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }

    [JsonPropertyName("manifest")]
    public OcrManifest? Manifest { get; init; }
}

public sealed record OcrManifest([property: JsonPropertyName("coverage")] OcrCoverage? Coverage);

public sealed record OcrCoverage([property: JsonPropertyName("failed")] IReadOnlyList<OcrCoverageItem>? Failed);

public sealed record OcrCoverageItem(
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("classification")] string? Classification,
    [property: JsonPropertyName("reason")] string? Reason);
