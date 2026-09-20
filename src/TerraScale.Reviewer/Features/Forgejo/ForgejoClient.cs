using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using TerraScale.Reviewer.Features.Configuration;

namespace TerraScale.Reviewer.Features.Forgejo;

public sealed class ForgejoClient(HttpClient http, ReviewerOptions options)
{
    private readonly HttpClient _http = Configure(http, options);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static HttpClient Configure(HttpClient client, ReviewerOptions options)
    {
        client.BaseAddress = new Uri(options.ForgejoUrl + "/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", options.ForgejoToken);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    public Task<PullRequest?> PullAsync(string owner, string name, int number, CancellationToken ct) =>
        GetAsync<PullRequest>($"api/v1/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/pulls/{number}", ct);
    public async Task<IReadOnlyList<ChangedFile>> FilesAsync(string owner, string name, int number, CancellationToken ct) =>
        await GetAsync<List<ChangedFile>>($"api/v1/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/pulls/{number}/files?limit=100", ct) ?? [];
    public async Task<string> DiffAsync(string owner, string name, int number, CancellationToken ct)
    {
        using var response = await _http.GetAsync($"api/v1/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/pulls/{number}.diff", HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccess(response, "diff", ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, ct);
        if (memory.Length > 8 * 1024 * 1024) throw new InvalidOperationException("pull request diff exceeds limit");
        return System.Text.Encoding.UTF8.GetString(memory.ToArray());
    }
    public async Task<IReadOnlyList<IssueComment>> CommentsAsync(string owner, string name, int number, CancellationToken ct) =>
        await GetAsync<List<IssueComment>>($"api/v1/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/issues/{number}/comments?limit=100", ct) ?? [];
    public Task CommentAsync(string owner, string name, int number, string body, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"api/v1/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/issues/{number}/comments", new { body }, ct);
    public Task ReactToPullRequestAsync(string owner, string name, int number, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"api/v1/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/issues/{number}/reactions", new { content = "eyes" }, ct);
    public Task ReactToCommentAsync(string owner, string name, long commentId, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"api/v1/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/issues/comments/{commentId}/reactions", new { content = "eyes" }, ct);
    public Task UpdateCommentAsync(string owner, string name, long id, string body, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, $"api/v1/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/issues/comments/{id}", new { body }, ct);
    public Task ReviewAsync(string owner, string name, int number, PullReview review, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"api/v1/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/pulls/{number}/reviews", review, ct);
    public Task StatusAsync(string owner, string name, string sha, CommitStatus status, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"api/v1/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/statuses/{Uri.EscapeDataString(sha)}", status, ct);
    public async Task<IReadOnlyList<RepositoryTreeEntry>> TreeAsync(string owner, string name, string sha, CancellationToken ct) =>
        (await GetAsync<RepositoryTreeResponse>($"api/v1/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/git/trees/{Uri.EscapeDataString(sha)}?recursive=true", ct))?.Tree ?? [];
    public async Task<string> FileAsync(string owner, string name, string sha, string path, CancellationToken ct)
    {
        var content = await GetAsync<RepositoryContent>($"api/v1/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/contents/{EscapePath(path)}?ref={Uri.EscapeDataString(sha)}", ct)
            ?? throw new InvalidOperationException($"Forgejo returned no content for {path}");
        if (!string.Equals(content.Type, "file", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(content.Content)) return string.Empty;
        return string.Equals(content.Encoding, "base64", StringComparison.OrdinalIgnoreCase)
            ? Encoding.UTF8.GetString(Convert.FromBase64String(content.Content.Replace("\n", string.Empty, StringComparison.Ordinal)))
            : content.Content;
    }

    private async Task<T?> GetAsync<T>(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(path, ct);
        await EnsureSuccess(response, path, ct);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
    }
    private Task SendAsync(HttpMethod method, string path, object body, CancellationToken ct) => SendAsync<object>(method, path, body, ct);
    private async Task<T?> SendAsync<T>(HttpMethod method, string path, object body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body, options: JsonOptions) };
        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccess(response, path, ct);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
    }
    private static async Task EnsureSuccess(HttpResponseMessage response, string path, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = await response.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException($"forgejo {path}: status {(int)response.StatusCode}: {detail.Trim()}");
    }

    private static string EscapePath(string path) => string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
}
