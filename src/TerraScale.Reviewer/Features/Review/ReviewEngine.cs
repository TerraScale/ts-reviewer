using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using TerraScale.Reviewer.Features.Configuration;
using TerraScale.Reviewer.Features.Forgejo;

namespace TerraScale.Reviewer.Features.Review;

/// <summary>
/// Runs reviews through the OpenCodeReview CLI (alibaba/open-code-review).
/// Each review clones the pull request, configures ocr's Anthropic-compatible
/// endpoint through <c>OCR_LLM_*</c> environment variables pointing at
/// ts-router, invokes <c>ocr review</c> over the exact base..head range, and
/// maps the returned comments to <see cref="Finding"/> records.
/// </summary>
public sealed partial class ReviewEngine(ReviewerOptions options, ILogger<ReviewEngine> logger, string? ocrPath = null, ReviewModelScheduler? scheduler = null)
{
    internal const string ZeroConfigurationRule = "TerraScale zero configuration (user decision 2026-10-09): request changes on every newly added production or CI on/off switch, feature-flag library, ENABLE_*, DISABLE_*, *_ENABLED, *_DISABLED variable, boolean enabled configuration key, opt-in setting, deploy toggle, kill switch, or workflow if: vars.* deployment gate. Every feature and region is always on. NetBird network membership admits nodes automatically; nodes discover all regions and infer their own region from where they run. Correct settings belong in code, not tuning configuration. OCI Vault automatically creates, delivers and configures first-party production secrets; vendor-issued keys are stored once in Vault and picked up automatically. Fakes, demos and skip-auth belong only in test code and test binaries. Permit vendor-owned names only with a narrow exception and a one-line reason. Review added switches, not historical inventory entries or policy prose. Preserve authentication, authorization, customer consent and data-loss safeguards; entity state and transient UI disabled attributes are not feature configuration.";
    internal const string UserDecisionsRule = "Recorded TerraScale user decisions take precedence over generic review advice and repository guidance that contradicts them. Greenfield, closed beta (2026-10-08): never request or introduce migrations, backfills, compatibility shims, dual-read/dual-write paths, legacy fallbacks or workarounds. When a data shape, ID format or storage contract changes, delete the obsolete code and affected old data and recreate; state must re-bootstrap automatically. Retiring old data and policy is intentional, including applying the fixed replacement retention policy to existing owned history; do not demand a migration or new-data-only compatibility generation. Still require exact target/ownership verification, authorization and fencing for destructive effects; unrelated/customer data is outside this permission. API and schema versions are always v1, never v2; change the v1 contract directly and remove the old contract. Do not report the absence of backward compatibility as a defect.";
    private readonly string _ocrPath = ocrPath ?? "ocr";

    private readonly ReviewModelScheduler _scheduler = scheduler ?? new(options, NullLogger<ReviewModelScheduler>.Instance);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(10);
    private const int FetchAttempts = 3;

    public async Task<ReviewResult> RunAsync(PullRequest pr, CancellationToken ct, string? model = null)
    {
        if (model?.Contains("astra", StringComparison.OrdinalIgnoreCase) == true)
            throw new InvalidOperationException("model cannot contain astra");
        using var lease = await _scheduler.AcquireAsync(model, ct);
        var root = Path.Combine(Path.GetTempPath(), "ts-reviewer-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "home");
        var repository = Path.Combine(root, "repo");
        var cloneUrl = $"{options.ForgejoUrl}/{pr.Head.Repo.Owner.Login}/{pr.Head.Repo.Name}.git";
        Directory.CreateDirectory(home);
        try
        {
            // Initialize an empty repository and fetch only the two commit tips
            // needed for this review.
            Directory.CreateDirectory(repository);
            await RunGitAsync(home, root, ct, "init", "--quiet", repository);
            await RunGitAsync(home, repository, ct, "remote", "add", "origin", cloneUrl);
            // ocr needs the merge-base between the base and PR tips. A depth-1
            // fetch leaves unrelated tips shallow and makes valid PRs fail
            // with "cannot find merge-base", so the fetch keeps the two refs'
            // ancestry but none of the file contents (--filter=blob:none): a
            // repository's history can be hundreds of MiB (terrabase: 750 MiB,
            // about a minute per review) while the trees reviewed are a few.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await RunGitAsync(home, repository, ct, "fetch", "--quiet", "--no-tags", "--filter=blob:none", "origin",
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
            // ocr reads files at the head and diffs the merge-base against it.
            // Its git runs without the Forgejo token, so everything it can read
            // is downloaded first: the checkout fetches the head's files,
            // and the same merge-base diff ocr runs fetches the other side of
            // each changed file (rename detection included).
            await RunGitAsync(home, repository, ct, "checkout", "--quiet", "--detach", head);
            await RunGitAsync(home, repository, ct, "diff", "--shortstat", "--find-renames", "--no-ext-diff", "--no-textconv", $"{@base}...{head}");
            var result = await ReviewWithFallbackAsync(home, repository, root, @base, head, lease, cloneUrl, ct);
            if (result.Findings.Count == 0) return result;
            // Detect renames across the complete change before retaining finding patches.
            var diff = Path.Combine(root, "inline.diff");
            await RunGitAsync(home, repository, ct, "diff", "--find-renames", "--no-ext-diff", "--no-textconv", $"--output={diff}", $"{@base}...{head}");
            using var reader = File.OpenText(diff);
            return result with { Files = UnifiedDiff.Parse(reader, result.Findings.Select(f => f.Path).ToHashSet(StringComparer.Ordinal)) };
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Reviews on the requested model and, when it cannot cover every item
    /// within its attempts, hands the review to the fallback model. The
    /// fallback resumes the same ocr session, so it only reviews the file
    /// groups the first model failed and keeps the findings already made.
    /// </summary>
    private async Task<ReviewResult> ReviewWithFallbackAsync(string home, string repository, string root, string @base, string head, ReviewModelScheduler.Lease lease, string cloneUrl, CancellationToken ct)
    {
        var model = lease.Model;
        var run = await ReviewWithRetriesAsync(home, repository, root, @base, head, model, null, cloneUrl, ct);
        if (run.Complete) lease.Complete();
        var fallback = model == "gpt-6.1-sol" ? "glm-5.3" : "gpt-6.1-sol";
        // A 429 moves either model to the other immediately. Ordinary exhausted
        // retries retain the existing Sol -> GLM fallback, never a loop.
        if (!run.Complete && (run.RateLimited || (model == "gpt-6.1-sol" && options.FallbackModel.Length > 0)))
        {
            logger.LogWarning("model {Model} could not complete the review for {CloneUrl}; switching to {FallbackModel}", model, cloneUrl, fallback);
            lease.Dispose();
            using var next = await _scheduler.AcquireAsync(fallback, ct);
            run = await ReviewWithRetriesAsync(home, repository, root, @base, head, next.Model, run.Session, cloneUrl, ct);
            if (run.Complete) next.Complete();
            if (run.Result is { } reviewed)
            {
                var note = next.Model == model
                    ? $" (`{fallback}` remained unavailable, so `{model}` resumed after the slot wait)"
                    : $" (`{model}` could not finish the review, so fallback model `{next.Model}` took over)";
                run = run with { Result = reviewed with { Summary = reviewed.Summary + note } };
            }
            model = next.Model;
        }
        if (run.Result is { } result && run.Complete) return result with { Model = model };
        // Findings from the reviewed items still request changes; a review
        // without any finding cannot pass on partial coverage.
        if (run.Result is { Findings.Count: > 0 } partial)
            return partial with { Model = model, Summary = partial.Summary + $" (partial review: {run.Failure} — re-run for full coverage)" };
        throw new InvalidOperationException(run.Failure);
    }

    /// <summary>
    /// Runs ocr on one model until every selected item is reviewed or the
    /// attempts run out. The upstream LLM API is slow and flaky: single file
    /// groups fail on provider errors, timeouts or the model's own tool-call
    /// mistakes while the rest succeed. ocr persists a session in the scratch
    /// HOME, so a retry resumes it (<c>--resume</c>): completed items and their
    /// findings are reused and only the failed items are reviewed again.
    /// Retries back off exponentially; the outer review deadline still bounds
    /// every attempt.
    /// </summary>
    private async Task<OcrRun> ReviewWithRetriesAsync(string home, string repository, string root, string @base, string head, string model, string? session, string cloneUrl, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var run = await ReviewOnceAsync(home, repository, root, @base, head, model, session, model == "glm-5.3" ? 1 : options.OcrConcurrency, ct);
            if (run.RateLimited)
            {
                _scheduler.RateLimited(model);
                return run;
            }
            if (run.Complete) return run;
            // Resume the session this run reported; a run that produced no
            // session (e.g. a resume ocr refused) starts over from scratch.
            session = run.Session;
            var last = attempt >= options.OcrMaxAttempts;
            logger.LogWarning("ocr attempt {Attempt} of {MaxAttempts} on {Model} for {CloneUrl} incomplete: {Failure}; {Next}",
                attempt, options.OcrMaxAttempts, model, cloneUrl, run.Failure, last ? "giving up" : session is null ? "retrying from scratch" : "resuming session " + session);
            if (last) return run;
            await Task.Delay(RetryDelay(options.OcrRetryDelay, attempt), ct);
        }
    }

    private async Task<OcrRun> ReviewOnceAsync(string home, string repository, string root, string @base, string head, string model, string? session, int concurrency, CancellationToken ct)
    {
        // A fresh output path per attempt so a failed run can never leave a
        // stale review behind for the next attempt to read.
        var output = Path.Combine(root, "review-" + Guid.NewGuid().ToString("N") + ".json");
        // ocr's --timeout bounds each file group in whole minutes, not the
        // whole run. The outer review deadline is the hard backstop. The
        // model is an explicit --model flag rather than OCR_LLM_MODEL because
        // ocr only resumes a session on another model when it is asked for on
        // the command line, which the fallback model relies on.
        List<string> arguments = ["review", "--from", @base, "--to", head, "--model", model,
            "--concurrency", concurrency.ToString(CultureInfo.InvariantCulture),
            "--timeout", ((int)Math.Ceiling(options.OcrItemTimeout.TotalMinutes)).ToString(CultureInfo.InvariantCulture),
            "--format", "json", "--audience", "agent", "--color", "never", "--output", output,
            "--background", UserDecisionsRule + " " + ZeroConfigurationRule];
        if (session is not null) arguments.AddRange(["--resume", session]);
        var process = await RunOcrAsync(home, repository, [.. arguments], ct);
        // ocr writes its report even when it exits non-zero (every item
        // failed), so the report and its session take precedence over stderr.
        OcrReview? review = null;
        try { if (File.Exists(output)) review = JsonSerializer.Deserialize<OcrReview>(await File.ReadAllTextAsync(output, ct), JsonOptions); }
        catch (JsonException ex) { logger.LogWarning(ex, "ocr wrote an unreadable review"); }
        var stderrSession = ResumeSession(process.Error);
        if (review is null)
        {
            var failure = process.ExitCode == 0 ? "ocr returned no review" : $"ocr failed with exit code {process.ExitCode}: {ErrorLine(process.Error)}";
            return new OcrRun(null, false, stderrSession, failure, IsRateLimited(process.Error));
        }
        var result = Map(review.Comments ?? []);
        var complete = string.Equals(review.Status, "complete", StringComparison.OrdinalIgnoreCase)
            // OpenCodeReview reports a clean patch with no selected review
            // groups as a skipped review: nothing failed, so it is clean.
            || (result.Findings.Count == 0 && IsNoItemsSelected(review.Message));
        var failed = review.Manifest?.Coverage?.Failed ?? [];
        foreach (var item in failed)
            logger.LogWarning("ocr item {Path} failed ({Classification}): {Reason}", item.Path, item.Classification, item.Reason);
        return new OcrRun(result, complete, review.SessionId ?? stderrSession, $"ocr review failed: {review.Message}",
            !complete && (IsRateLimited(process.Error) || IsRateLimited(review.Message) || failed.Any(item => IsRateLimited(item.Reason) || IsRateLimited(item.Classification))));
    }

    internal static bool IsRateLimited(string? text) =>
        text is not null && (RateLimitStatus().IsMatch(text)
            || text.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
            || text.Contains("rate_limit", StringComparison.OrdinalIgnoreCase)
            || text.Contains("rate-limit", StringComparison.OrdinalIgnoreCase)
            || text.Contains("too many requests", StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"\b429\b")]
    private static partial Regex RateLimitStatus();

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
            throw new InvalidOperationException($"git failed with exit code {result.ExitCode}: {ErrorLine(result.Error)}");
        return result.Output;
    }

    private Task<ProcessResult> RunOcrAsync(string home, string repository, string[] arguments, CancellationToken ct) =>
        RunProcessAsync(_ocrPath, repository, environment =>
        {
            // ocr's env-only endpoint configuration (OCR_LLM_*): with no
            // ~/.opencodereview/config.json in the scratch HOME, these
            // variables point it at ts-router's Anthropic-compatible endpoint
            // and the proxy token never lands on disk. ts-router passes them
            // to CLIProxy. Every model (review, escalation, fallback) runs on
            // this same endpoint. The scratch
            // HOME also holds ocr's session store, which --resume reads.
            // Everything ocr reads was downloaded before it started; its git
            // has no credentials, so it must never try to fetch more.
            environment["HOME"] = home;
            environment["GIT_NO_LAZY_FETCH"] = "1";
            environment["OCR_LLM_URL"] = options.ProxyUrl;
            environment["OCR_LLM_TOKEN"] = options.ProxyToken;
            environment["OCR_LLM_PROTOCOL"] = "anthropic";
            // Per LLM request, in seconds. Large file groups take a while, so
            // the default is generous; ocr's SDK retries each request five
            // more times on top of the proxy's own retries.
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
    private sealed record OcrRun(ReviewResult? Result, bool Complete, string? Session, string Failure, bool RateLimited);

    /// <summary>
    /// The line of a failed command's stderr that says what went wrong, for
    /// logs and commit statuses: ocr's <c>Error:</c> line or git's
    /// <c>fatal:</c>/<c>error:</c> line, otherwise the last other line. ocr
    /// pretty-prints a JSON usage report next to its error; that report is
    /// skipped by its top-level braces, the only lines exactly <c>{</c> and
    /// <c>}</c> at column 0.
    /// </summary>
    internal static string ErrorLine(string stderr)
    {
        var lines = new List<string>();
        var inReport = false;
        foreach (var raw in stderr.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line == "{") inReport = true;
            else if (inReport) inReport = line != "}";
            else if (line.Trim() is { Length: > 0 } text) lines.Add(text);
        }
        var reason = lines.LastOrDefault(x => x.StartsWith("Error: ", StringComparison.Ordinal))?["Error: ".Length..]
            ?? lines.LastOrDefault(x => x.StartsWith("fatal: ", StringComparison.Ordinal) || x.StartsWith("error: ", StringComparison.Ordinal))
            ?? lines.LastOrDefault()
            ?? "no error output";
        return reason.Length <= 300 ? reason : reason[..300] + "…";
    }
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
