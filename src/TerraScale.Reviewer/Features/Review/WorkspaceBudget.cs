using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using TerraScale.Reviewer.Features.Configuration;
using TerraScale.Reviewer.Features.Context;

namespace TerraScale.Reviewer.Features.Review;

internal interface IWorkspaceBudget
{
    Task<string> SelectModelAsync(CancellationToken ct);
    void ConfirmWorkspace(string? workspace);
}

internal sealed class WorkspaceBudget(ReviewerOptions options, HttpClient http, ContextStore store, TimeProvider time, ILogger<WorkspaceBudget> logger) : IWorkspaceBudget
{
    internal const string Sonnet = "claude-sonnet-5-5";
    internal const string Haiku = "claude-haiku-5-5";
    private readonly SemaphoreSlim _refresh = new(1);
    private DateTimeOffset _fetchedAt;
    private DateTime _day;
    private decimal _cost;
    private string? _failure;
    private int _identityFailed;
    private sealed class BillingException(string reason) : InvalidOperationException(reason);

    public async Task<string> SelectModelAsync(CancellationToken ct)
    {
        await _refresh.WaitAsync(ct);
        try
        {
            while (true)
            {
                if (Volatile.Read(ref _identityFailed) != 0) throw new InvalidOperationException("Anthropic inference workspace identity failed; all new model calls stopped without a verdict");
                var now = time.GetUtcNow();
                var age = now - _fetchedAt;
                if (_day == now.UtcDateTime.Date && age >= TimeSpan.Zero && age < TimeSpan.FromSeconds(60)) break;
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    var cost = await FetchAsync(now.UtcDateTime.Date, timeout.Token);
                    // A report fetched across midnight cannot authorize the next day's spending.
                    if (time.GetUtcNow().UtcDateTime.Date != now.UtcDateTime.Date) continue;
                    _cost = store.RecordWorkspaceCost(options.AnthropicWorkspaceId, now.ToString("yyyy-MM", CultureInfo.InvariantCulture), cost);
                    _failure = null;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    // Cache failures too: sixteen workers must not hammer a failing control plane.
                    _failure = ex is BillingException ? ex.Message : "transport failure or malformed report";
                    logger.LogWarning("Anthropic workspace billing unavailable: {Reason}; no model call authorized", _failure);
                }
                _day = now.UtcDateTime.Date;
                _fetchedAt = time.GetUtcNow();
                break;
            }
            if (_failure is not null) throw new InvalidOperationException("Anthropic workspace billing unavailable: " + _failure + "; review stopped without a verdict");
            if (Volatile.Read(ref _identityFailed) != 0) throw new InvalidOperationException("Anthropic inference workspace identity failed; all new model calls stopped without a verdict");
            if (_cost >= 200m) throw new InvalidOperationException("Anthropic workspace monthly $200 spending authorization exhausted; review stopped without a verdict");
            return _cost >= 150m ? Haiku : _cost >= 100m ? Sonnet : ReviewProvider.PrimaryModel;
        }
        finally { _refresh.Release(); }
    }

    private async Task<decimal> FetchAsync(DateTime today, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.AnthropicAdminApiKey) || string.IsNullOrWhiteSpace(options.AnthropicWorkspaceId))
            throw new BillingException("billing credential or workspace identity missing");
        using (var workspace = await GetAsync("/v1/organizations/workspaces/" + Uri.EscapeDataString(options.AnthropicWorkspaceId), ct))
        {
            var root = workspace.RootElement;
            if (root.GetProperty("id").GetString() != options.AnthropicWorkspaceId
                || root.GetProperty("archived_at").ValueKind != JsonValueKind.Null) throw new BillingException("workspace identity mismatched or archived");
        }
        await ConfirmInferenceKeyAsync(ct);
        // The default's real ID is returned by identity APIs, but cost reports use null for it.
        var isDefault = !await ListedNonDefaultAsync(ct);
        var first = new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        // The user accepts unpublished current-day costs; include them as soon as the provider reports them.
        var path = "/v1/organizations/cost_report?starting_at=" + Uri.EscapeDataString(first.ToString("O"))
            + "&ending_at=" + Uri.EscapeDataString(today.AddDays(1).AddSeconds(1).ToString("O"))
            + "&bucket_width=1d&limit=31&group_by%5B%5D=workspace_id";
        var days = new HashSet<DateTime>();
        var pages = new HashSet<string>(StringComparer.Ordinal);
        string? page = null;
        decimal cents = 0;
        do
        {
            using var report = await GetAsync(path + (page is null ? "" : "&page=" + Uri.EscapeDataString(page)), ct);
            var root = report.RootElement;
            foreach (var bucket in root.GetProperty("data").EnumerateArray())
            {
                var start = bucket.GetProperty("starting_at").GetDateTimeOffset().UtcDateTime;
                var end = bucket.GetProperty("ending_at").GetDateTimeOffset().UtcDateTime;
                if (start != start.Date || start < first || start > today || end != start.AddDays(1) || !days.Add(start))
                    throw new BillingException("duplicate or invalid daily bucket");
                foreach (var result in bucket.GetProperty("results").EnumerateArray())
                {
                    var workspace = result.GetProperty("workspace_id").GetString();
                    if (result.GetProperty("currency").GetString() != "USD"
                        || !decimal.TryParse(result.GetProperty("amount").GetString(), NumberStyles.AllowDecimalPoint,
                            CultureInfo.InvariantCulture, out var amount) || amount < 0) throw new BillingException("invalid currency or amount");
                    if (workspace == (isDefault ? null : options.AnthropicWorkspaceId)) cents += amount;
                }
            }
            if (!root.GetProperty("has_more").GetBoolean()) break;
            page = root.GetProperty("next_page").GetString();
            if (string.IsNullOrWhiteSpace(page) || !pages.Add(page) || pages.Count > 31) throw new BillingException("invalid pagination");
        } while (true);
        var completedDays = days.Count - (days.Contains(today) ? 1 : 0);
        if (completedDays != today.Day - 1) throw new BillingException($"completed daily coverage {completedDays}/{today.Day - 1}");
        return cents / 100m;
    }

    public void ConfirmWorkspace(string? workspace)
    {
        if (Volatile.Read(ref _identityFailed) != 0) throw new BillingException("inference workspace identity previously failed");
        if (workspace == options.AnthropicWorkspaceId && !string.IsNullOrWhiteSpace(workspace)) return;
        Interlocked.Exchange(ref _identityFailed, 1);
        throw new BillingException("inference workspace identity missing or mismatched; all new model calls stopped without a verdict");
    }

    private async Task ConfirmInferenceKeyAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, options.AnthropicUrl.TrimEnd('/') + "/v1/messages/count_tokens");
        request.Headers.Add("x-api-key", options.AnthropicApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Content = JsonContent.Create(new { model = ReviewProvider.PrimaryModel, messages = new[] { new { role = "user", content = "Workspace identity preflight" } } });
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new BillingException($"inference identity preflight HTTP {(int)response.StatusCode}");
        // This free endpoint must demonstrate the identity header in the live setup check; never probe with a paid call.
        ConfirmWorkspace(response.Headers.TryGetValues("anthropic-workspace-id", out var ids) ? string.Join(",", ids) : null);
    }

    private async Task<bool> ListedNonDefaultAsync(CancellationToken ct)
    {
        var path = "/v1/organizations/workspaces?include_default=false&include_archived=false&limit=1000";
        var pages = new HashSet<string>(StringComparer.Ordinal);
        string? after = null;
        var found = false;
        do
        {
            using var list = await GetAsync(path + (after is null ? "" : "&after_id=" + Uri.EscapeDataString(after)), ct);
            var root = list.RootElement;
            foreach (var workspace in root.GetProperty("data").EnumerateArray())
                if (workspace.GetProperty("id").GetString() == options.AnthropicWorkspaceId)
                {
                    if (workspace.GetProperty("archived_at").ValueKind != JsonValueKind.Null) throw new BillingException("workspace archived");
                    found = true;
                }
            if (!root.GetProperty("has_more").GetBoolean()) return found;
            after = root.GetProperty("last_id").GetString();
            if (string.IsNullOrWhiteSpace(after) || !pages.Add(after) || pages.Count > 100) throw new BillingException("invalid workspace pagination");
        } while (true);
    }

    private async Task<JsonDocument> GetAsync(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, options.AnthropicUrl.TrimEnd('/') + path);
        request.Headers.Add("x-api-key", options.AnthropicAdminApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new BillingException($"HTTP {(int)response.StatusCode}");
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(body, cancellationToken: ct);
    }
}
