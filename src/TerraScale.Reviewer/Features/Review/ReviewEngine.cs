using System.ComponentModel;
using System.Diagnostics;
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
public sealed class ReviewEngine(ReviewerOptions options, ILogger<ReviewEngine> logger, string? ocrPath = null)
{
    private readonly string _ocrPath = ocrPath ?? "ocr";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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
            await RunGitAsync(home, repository, ct, "fetch", "--quiet", "--no-tags", "origin",
                $"+refs/heads/{pr.Base.Ref}:refs/base", $"+refs/pull/{pr.Number}/head:refs/pr-head");
            var head = (await RunGitAsync(home, repository, ct, "rev-parse", "refs/pr-head")).Trim();
            var @base = (await RunGitAsync(home, repository, ct, "rev-parse", "refs/base")).Trim();
            if (!string.Equals(head, pr.Head.Sha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("pull request head moved before the review started");
            await RunGitAsync(home, repository, ct, "checkout", "--quiet", "--detach", head);
            var output = Path.Combine(root, "review.json");
            // ocr's --timeout bounds each concurrent subtask in minutes, not
            // the whole run; 15 accommodates 180s per-request LLM timeouts
            // with the SDK's retry backoff. The outer ReviewTimeout deadline
            // is the hard backstop and a review that outlives it fails like
            // any other engine error.
            await RunOcrAsync(home, repository, model,
                ["review", "--from", @base, "--to", head, "--concurrency", options.OcrConcurrency.ToString(),
                 "--timeout", "15", "--format", "json", "--audience", "agent", "--color", "never", "--output", output], ct);
            var review = JsonSerializer.Deserialize<OcrReview>(await File.ReadAllTextAsync(output, ct), JsonOptions)
                ?? throw new InvalidOperationException("ocr returned no review");
            var result = Map(review.Comments ?? []);
            if (!string.Equals(review.Status, "complete", StringComparison.OrdinalIgnoreCase))
            {
                // OpenCodeReview reports a clean patch with no selected review
                // groups as a skipped review. There is no failure to surface
                // when it selected nothing, so preserve the empty result and
                // publish the normal successful review status.
                if (result.Findings.Count == 0 && IsNoItemsSelected(review.Message)) return result;
                if (result.Findings.Count == 0) throw new InvalidOperationException($"ocr review failed: {review.Message}");
                logger.LogWarning("ocr review finished {Status} for {CloneUrl}: {Message}", review.Status, cloneUrl, review.Message);
                result = result with { Summary = result.Summary + " (partial review: some review groups failed — re-run for full coverage)" };
            }
            return result;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
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

    private Task<string> RunGitAsync(string home, string working, CancellationToken ct, params string[] arguments) =>
        RunAsync("git", working, environment =>
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

    private Task<string> RunOcrAsync(string home, string repository, string? model, string[] arguments, CancellationToken ct) =>
        RunAsync(_ocrPath, repository, environment =>
        {
            // ocr's env-only endpoint configuration (OCR_LLM_*): with no
            // ~/.opencodereview/config.json in the scratch HOME, these four
            // variables point it at the CLIProxy OpenAI-compatible endpoint
            // and the proxy token never lands on disk. A command-triggered
            // escalation swaps the model on the same endpoint.
            environment["HOME"] = home;
            environment["OCR_LLM_URL"] = options.ProxyUrl;
            environment["OCR_LLM_TOKEN"] = options.ProxyToken;
            environment["OCR_LLM_MODEL"] = model ?? options.Model;
            environment["OCR_LLM_PROTOCOL"] = "openai";
            // 180s per LLM request: the upstream zai GLM API is slow and
            // flaky; ocr's SDK default is a 5-minute timeout with 5 retries.
            environment["OCR_LLM_TIMEOUT"] = "180";
            environment["OCR_ENABLE_TELEMETRY"] = "false";
        }, ct, arguments);

    private static async Task<string> RunAsync(string fileName, string workingDirectory, Action<IDictionary<string, string?>> configureEnvironment, CancellationToken ct, string[] arguments)
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
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var error = await stderr;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{fileName} failed with exit code {process.ExitCode}: {Truncate(error)}");
        return await stdout;
    }

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
}
