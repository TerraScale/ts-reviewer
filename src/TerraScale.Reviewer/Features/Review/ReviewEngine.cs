using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
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
public sealed class ReviewEngine(ReviewerOptions options, ILogger<ReviewEngine> logger, string? ocrPath = null)
{
    internal const string ZeroConfigurationRule = "TerraScale zero configuration (user decision 2026-10-09): request changes on every newly added production or CI on/off switch, feature-flag library, ENABLE_*, DISABLE_*, *_ENABLED, *_DISABLED variable, boolean enabled configuration key, opt-in setting, deploy toggle, kill switch, or workflow if: vars.* deployment gate. Boolean declarations that act as operator on/off configuration require changes requested, regardless of name, visibility or default value: const/readonly bool flags, boolean configuration properties or parameters, and boolean environment/JSON/YAML values used to gate production or CI behavior. Follow how the value is supplied and used; a flag hardcoded to true is still a switch. Review this semantically even when CI has no switch scanner. Every feature and region is always on. NetBird network membership admits nodes automatically; nodes discover all regions and infer their own region from where they run. Correct settings belong in code, not tuning configuration. OCI Vault automatically creates, delivers and configures first-party production secrets; vendor-issued keys are stored once in Vault and picked up automatically. Fakes, demos and skip-auth belong only in test code and test binaries. Permit vendor-owned names only with a narrow exception and a one-line reason. Review added switches, not historical inventory entries or policy prose. Preserve authentication, authorization, customer consent and data-loss safeguards; entity state and transient UI disabled attributes are not feature configuration.";
    internal const string UserDecisionsRule = "Recorded TerraScale user decisions take precedence over generic review advice and repository guidance that contradicts them. Greenfield, closed beta (2026-10-08): never request or introduce migrations, backfills, compatibility shims, dual-read/dual-write paths, legacy fallbacks or workarounds. When a data shape, ID format or storage contract changes, delete the obsolete code and affected old data and recreate; state must re-bootstrap automatically. Retiring old data and policy is intentional, including applying the fixed replacement retention policy to existing owned history; do not demand a migration or new-data-only compatibility generation. Still require exact target/ownership verification, authorization and fencing for destructive effects; unrelated/customer data is outside this permission. API and schema versions are always v1, never v2; change the v1 contract directly and remove the old contract. Do not report the absence of backward compatibility as a defect.";
    private readonly string _ocrPath = ocrPath ?? "ocr";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int FetchAttempts = 3;

    public async Task<ReviewResult> RunAsync(PullRequest pr, CancellationToken ct)
    {
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
            await using var provider = new ReviewProvider(options, logger, $"{pr.Head.Repo.Owner.Login}/{pr.Head.Repo.Name}#{pr.Number}@{head}");
            await provider.StartAsync(ct);
            var run = await ReviewOnceAsync(home, repository, root, @base, head, provider, ct);
            if (provider.Failure is { } failure) throw new InvalidOperationException(failure);
            if (!run.Complete || run.Result is null) throw new InvalidOperationException(run.Failure);
            var result = run.Result with { Model = provider.ModelsUsed };
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

    private async Task<OcrRun> ReviewOnceAsync(string home, string repository, string root, string @base, string head, ReviewProvider provider, CancellationToken ct)
    {
        var output = Path.Combine(root, "review.json");
        List<string> arguments = ["review", "--from", @base, "--to", head, "--model", ReviewProvider.PrimaryModel,
            "--concurrency", "1", "--timeout", ReviewProvider.StepTimeout.TotalMinutes.ToString(CultureInfo.InvariantCulture),
            "--format", "json", "--audience", "agent", "--color", "never", "--output", output,
            "--background", UserDecisionsRule + " " + ZeroConfigurationRule];
        var process = await RunOcrAsync(home, repository, provider, [.. arguments], ct);
        // ocr writes its report even when it exits non-zero (every item
        // failed), so coverage must be checked independently of its exit code.
        OcrReview? review = null;
        try { if (File.Exists(output)) review = JsonSerializer.Deserialize<OcrReview>(await File.ReadAllTextAsync(output, ct), JsonOptions); }
        catch (JsonException ex) { logger.LogWarning(ex, "ocr wrote an unreadable review"); }
        if (review is null)
        {
            var failure = process.ExitCode == 0 ? "ocr returned no review" : $"ocr failed with exit code {process.ExitCode}: {ErrorLine(process.Error)}";
            return new OcrRun(null, false, failure);
        }
        var result = Map(review.Comments ?? []);
        var complete = string.Equals(review.Status, "complete", StringComparison.OrdinalIgnoreCase)
            // OpenCodeReview reports a clean patch with no selected review
            // groups as a skipped review: nothing failed, so it is clean.
            || (result.Findings.Count == 0 && IsNoItemsSelected(review.Message));
        var failed = review.Manifest?.Coverage?.Failed ?? [];
        foreach (var item in failed)
            logger.LogWarning("ocr item {Path} failed ({Classification}): {Reason}", item.Path, item.Classification, item.Reason);
        return new OcrRun(result, process.ExitCode == 0 && complete && failed.Count == 0,
            $"ocr review failed: {review.Message ?? ErrorLine(process.Error)}");
    }

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

    private Task<ProcessResult> RunOcrAsync(string home, string repository, ReviewProvider provider, string[] arguments, CancellationToken ct) =>
        RunProcessAsync(_ocrPath, repository, environment =>
        {
            environment["HOME"] = home;
            environment["GIT_NO_LAZY_FETCH"] = "1";
            environment["OCR_LLM_URL"] = provider.Url;
            environment["OCR_LLM_TOKEN"] = provider.Token;
            environment["OCR_LLM_PROTOCOL"] = "anthropic";
            // The local endpoint owns all retries, including response validation.
            environment["OCR_LLM_TIMEOUT"] = ReviewProvider.StepTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture);
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

    /// <summary>One OCR run and whether it covered every selected item.</summary>
    private sealed record OcrRun(ReviewResult? Result, bool Complete, string Failure);

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
