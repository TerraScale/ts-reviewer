using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TerraScale.TsReviewer.Features.Configuration;
using TerraScale.TsReviewer.Features.Forgejo;

namespace TerraScale.TsReviewer.Features.Review;

public sealed class ReviewEngine(HttpClient http, ReviewerOptions options)
{
    private static readonly Regex FencedJson = new("```(?:json)?\\s*(.*?)```", RegexOptions.Singleline | RegexOptions.Compiled);
    private readonly HttpClient _http = Configure(http, options);
    private readonly ReviewerOptions _options = options;
    private static HttpClient Configure(HttpClient client, ReviewerOptions options)
    {
        client.BaseAddress = new Uri(options.ProxyUrl + "/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ProxyToken);
        return client;
    }

    public async Task<ReviewResult> RunAsync(PullRequest pr, IReadOnlyList<ChangedFile> files, CancellationToken ct)
    {
        var diff = new StringBuilder();
        foreach (var file in files)
            diff.Append("FILE ").Append(file.Filename).Append(" (").Append(file.Status).Append(" +").Append(file.Additions).Append(" -").Append(file.Deletions).Append(")\n").Append(file.Patch).Append("\n\n");
        if (Encoding.UTF8.GetByteCount(diff.ToString()) > _options.MaxDiffBytes) throw new InvalidOperationException("pull request diff exceeds review limit");
        var passes = new[] { "correctness, data integrity, concurrency, and error handling", "security, authorization, secret exposure, and injection", "API contracts, migrations, compatibility, and operational behavior", "tests, observability, failure recovery, and adversarial edge cases" };
        using var semaphore = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrent));
        var tasks = passes.Select(async focus => { await semaphore.WaitAsync(ct); try { return await PassAsync(pr, diff.ToString(), focus, ct); } finally { semaphore.Release(); } });
        var results = await Task.WhenAll(tasks);
        return await AdversarialPassAsync(Merge(results), diff.ToString(), ct);
    }

    private async Task<ReviewResult> PassAsync(PullRequest pr, string diff, string focus, CancellationToken ct)
    {
        var prompt = $"You are a severe senior reviewer. Review only the supplied patch. Do not invent files, APIs, runtime behavior, or vulnerabilities. Trace callers and invariants mentally. Report only actionable findings supported by patch evidence. A finding must include an exact changed path and 1-based line when possible, impact, and a concrete fix or test. Severity is critical, high, medium, or low. Use unrelated=true only when the defect is proven independent of this patch; include owning_repository only then. Return JSON only: {{\"summary\":string,\"findings\":[{{\"id\":string,\"severity\":string,\"title\":string,\"body\":string,\"fix\":string,\"path\":string,\"line\":number,\"confidence\":number,\"unrelated\":boolean,\"owning_repository\":string}}]}}. Focus: {focus}\nPR: {pr.Title}\n{diff}";
        return await ChatAsync(prompt, ct);
    }
    private async Task<ReviewResult> AdversarialPassAsync(ReviewResult candidates, string diff, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(candidates);
        var prompt = $"Act as the final adversarial reviewer. The candidate findings below came from several passes. Remove anything unsupported by the patch, duplicate, speculative, or merely stylistic. Preserve concrete correctness, security, data-integrity, and operational defects. Never invent a path or line. Return the same JSON schema and JSON only. Candidate findings: {payload}\nPATCH EVIDENCE:\n{diff}";
        return Merge([await ChatAsync(prompt, ct)]);
    }
    private async Task<ReviewResult> ChatAsync(string prompt, CancellationToken ct)
    {
        var request = new { model = _options.Model, temperature = 0.1, max_tokens = 12000, messages = new[] { new { role = "system", content = "You are a code-review engine. Follow the requested JSON schema exactly." }, new { role = "user", content = prompt } } };
        using var response = await _http.PostAsJsonAsync("chat/completions", request, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"cliproxy status {(int)response.StatusCode}: {raw.Trim()}");
        using var document = JsonDocument.Parse(raw);
        var content = document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(content)) throw new InvalidOperationException("cliproxy returned no choices");
        var fenced = FencedJson.Match(content);
        if (fenced.Success) content = fenced.Groups[1].Value.Trim();
        return JsonSerializer.Deserialize<ReviewResult>(content) ?? throw new JsonException("review JSON was empty");
    }

    public static ReviewResult Merge(IEnumerable<ReviewResult> results)
    {
        var findings = new List<Finding>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var summary = new StringBuilder("Multi-pass review completed.");
        foreach (var result in results)
        {
            if (!string.IsNullOrWhiteSpace(result.Summary)) summary.Append(' ').Append(result.Summary);
            foreach (var finding in result.Findings)
            {
                var id = string.IsNullOrWhiteSpace(finding.Id) ? $"{finding.Path}:{finding.Line}:{finding.Title}" : finding.Id;
                if (finding.Confidence < 0.72 || !seen.Add(id + finding.Path + finding.Line)) continue;
                findings.Add(finding with { Id = id });
            }
        }
        return new ReviewResult(summary.ToString(), findings);
    }
}
