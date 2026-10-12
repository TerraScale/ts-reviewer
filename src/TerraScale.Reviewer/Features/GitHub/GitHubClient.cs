using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.Reviewer.Features.Configuration;

namespace TerraScale.Reviewer.Features.GitHub;

public sealed class GitHubClient
{
    private readonly HttpClient _http;
    private readonly ReviewerOptions _options;
    private readonly Func<string, string, CancellationToken, Task<string>>? _tokenFactory;
    private readonly ConcurrentDictionary<string, InstallationToken> _tokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public GitHubClient(HttpClient http, ReviewerOptions options) : this(http, options, null) { }

    // Tests supply credentials at the transport boundary; production always uses the GitHub App.
    internal GitHubClient(HttpClient http, ReviewerOptions options, Func<string, string, CancellationToken, Task<string>>? tokenFactory)
    {
        (_http, _options, _tokenFactory) = (http, options, tokenFactory);
        _http.BaseAddress = new Uri(options.GitHubUrl.TrimEnd('/') + "/");
        _http.DefaultRequestHeaders.Authorization = null;
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("TerraScale-ts-reviewer/1.0");
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<bool> IsAllowedAsync(string owner, string name, CancellationToken ct)
    {
        if (!ValidRepository(owner, name)) return false;
        try { _ = await GetInstallationTokenAsync(owner, name, ct); return true; }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity)
        { return false; }
    }

    public async Task<string> GetInstallationTokenAsync(string owner, string name, CancellationToken ct)
    {
        if (!ValidRepository(owner, name)) throw new InvalidOperationException("repository is outside the GitHub installation boundary");
        return await InstallationTokenAsync(name, ct);
    }

    public async Task<PullRequest?> PullAsync(string owner, string name, int number, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return await GetAsync<PullRequest>(owner, name, $"{Repo(owner, name)}/pulls/{number}", ct); }
            catch (HttpRequestException ex) when (attempt < 3
                && (ex.StatusCode is null or HttpStatusCode.TooManyRequests || (int)ex.StatusCode >= 500))
            { await Task.Delay(TimeSpan.FromSeconds(attempt), ct); }
        }
    }
    public async Task<IReadOnlyList<IssueComment>> CommentsAsync(string owner, string name, int number, CancellationToken ct)
    {
        var comments = new List<IssueComment>();
        for (var page = 1; ; page++)
        {
            var batch = await GetAsync<List<IssueComment>>(owner, name, $"{Repo(owner, name)}/issues/{number}/comments?per_page=100&page={page}", ct) ?? [];
            comments.AddRange(batch);
            if (batch.Count < 100) return comments;
        }
    }
    public Task CommentAsync(string owner, string name, int number, string body, CancellationToken ct) =>
        SendAsync(owner, name, HttpMethod.Post, $"{Repo(owner, name)}/issues/{number}/comments", new { body }, ct);
    public Task ReactToPullRequestAsync(string owner, string name, int number, CancellationToken ct) =>
        SendAsync(owner, name, HttpMethod.Post, $"{Repo(owner, name)}/issues/{number}/reactions", new { content = "eyes" }, ct);
    public Task ReactToCommentAsync(string owner, string name, long commentId, CancellationToken ct) =>
        SendAsync(owner, name, HttpMethod.Post, $"{Repo(owner, name)}/issues/comments/{commentId}/reactions", new { content = "eyes" }, ct);
    public Task UpdateCommentAsync(string owner, string name, long id, string body, CancellationToken ct) =>
        SendAsync(owner, name, HttpMethod.Patch, $"{Repo(owner, name)}/issues/comments/{id}", new { body }, ct);
    public Task ReviewAsync(string owner, string name, int number, PullReview review, CancellationToken ct) =>
        SendAsync(owner, name, HttpMethod.Post, $"{Repo(owner, name)}/pulls/{number}/reviews", review, ct);
    public async Task<IReadOnlyList<SubmittedReview>> ReviewsAsync(string owner, string name, int number, CancellationToken ct)
    {
        var reviews = new List<SubmittedReview>();
        for (var page = 1; ; page++)
        {
            var batch = await GetAsync<List<SubmittedReview>>(owner, name, $"{Repo(owner, name)}/pulls/{number}/reviews?per_page=100&page={page}", ct) ?? [];
            reviews.AddRange(batch);
            if (batch.Count < 100) return reviews;
        }
    }
    public Task DismissReviewAsync(string owner, string name, int number, long id, string message, CancellationToken ct) =>
        SendAsync(owner, name, HttpMethod.Put, $"{Repo(owner, name)}/pulls/{number}/reviews/{id}/dismissals", new { message }, ct);
    public Task<Repository?> RepoAsync(string owner, string name, CancellationToken ct) => GetAsync<Repository>(owner, name, Repo(owner, name), ct);
    public async Task<string?> BranchAsync(string owner, string name, string branch, CancellationToken ct) =>
        (await GetAsync<RepositoryBranch>(owner, name, $"{Repo(owner, name)}/branches/{Uri.EscapeDataString(branch)}", ct))?.Commit.Sha;
    public Task<RepositoryIssue?> IssueAsync(string owner, string name, int number, CancellationToken ct) =>
        GetAsync<RepositoryIssue>(owner, name, $"{Repo(owner, name)}/issues/{number}", ct);
    public async Task<IReadOnlyList<RepositoryIssue>> IssuesAsync(string owner, string name, CancellationToken ct, DateTimeOffset? since = null)
    {
        var issues = new List<RepositoryIssue>();
        for (var page = 1; ; page++)
        {
            var batch = await GetAsync<List<RepositoryIssue>>(owner, name,
                $"{Repo(owner, name)}/issues?creator={Uri.EscapeDataString(_options.BotLogin)}&state=all&per_page=100&page={page}"
                    + (since is null ? string.Empty : "&since=" + Uri.EscapeDataString(since.Value.UtcDateTime.ToString("O"))), ct) ?? [];
            issues.AddRange(batch);
            if (batch.Count < 100) return issues;
            if (page == 8) throw new InvalidOperationException("issue marker lookup exceeded its fixed page limit; creation was not attempted");
        }
    }
    public async Task<RepositoryIssue> CreateIssueAsync(string owner, string name, string title, string body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Repo(owner, name)}/issues") { Content = JsonContent.Create(new { title, body }, options: JsonOptions) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetInstallationTokenAsync(owner, name, ct));
        using var response = await _http.SendAsync(request, ct);
        EnsureSuccess(response);
        return await response.Content.ReadFromJsonAsync<RepositoryIssue>(JsonOptions, ct)
            ?? throw new InvalidOperationException("GitHub returned no created issue");
    }
    public async Task<RepositoryWorkflow?> WorkflowAsync(string owner, string name, string file, CancellationToken ct) =>
        await GetWithTokenAsync<RepositoryWorkflow>($"{Repo(owner, name)}/actions/workflows/{Uri.EscapeDataString(file)}", await ActionsTokenAsync(owner, name, ct), ct);
    public async Task DispatchWorkflowAsync(string owner, string name, string file, string branch, string expectedCommit, CancellationToken ct, Action? beforeSend = null)
    {
        if (expectedCommit.Length != 40 || !expectedCommit.All(Uri.IsHexDigit)) throw new ArgumentException("expected workflow commit must be a full SHA", nameof(expectedCommit));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Repo(owner, name)}/actions/workflows/{Uri.EscapeDataString(file)}/dispatches")
        { Content = JsonContent.Create(new { @ref = branch, inputs = new { expected_commit = expectedCommit } }, options: JsonOptions) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await ActionsTokenAsync(owner, name, ct));
        beforeSend?.Invoke();
        using var response = await _http.SendAsync(request, ct);
        EnsureSuccess(response);
    }
    public Task StatusAsync(string owner, string name, string sha, CommitStatus status, CancellationToken ct)
    {
        if (status.State is not ("pending" or "success" or "failure" or "error"))
            throw new ArgumentException("GitHub commit status must be pending, success, failure, or error", nameof(status));
        return SendAsync(owner, name, HttpMethod.Post, $"{Repo(owner, name)}/statuses/{Uri.EscapeDataString(sha)}",
            status with { Description = status.Description.Length > 140 ? status.Description[..140] : status.Description }, ct);
    }

    public async Task<bool> CanReviewAsync(string owner, string name, string login, CancellationToken ct)
    {
        if (!ValidSegment(login)) return false;
        try
        {
            var permission = await GetAsync<CollaboratorPermission>(owner, name,
                $"{Repo(owner, name)}/collaborators/{Uri.EscapeDataString(login)}/permission", ct);
            return permission?.Permission is "write" or "maintain" or "admin";
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound) { return false; }
    }

    /// <summary>The selected repositories of the app installation; no organization-wide search permission is needed.</summary>
    public async Task<IReadOnlyList<Repository>> SearchRepositoriesAsync(int page, int limit, CancellationToken ct)
    {
        var token = await InstallationTokenAsync(string.Empty, ct);
        var response = await GetWithTokenAsync<InstallationRepositories>($"installation/repositories?per_page={limit}&page={page}", token, ct);
        // Preserve page length, including archived repositories, so callers do not stop before the next page.
        return response?.Repositories ?? [];
    }
    public async Task<IReadOnlyList<PullRequest>> OpenPullsAsync(string owner, string name, int page, int limit, CancellationToken ct) =>
        await GetAsync<List<PullRequest>>(owner, name, $"{Repo(owner, name)}/pulls?state=open&page={page}&per_page={limit}", ct) ?? [];
    public Task<CombinedStatus?> CombinedStatusAsync(string owner, string name, string sha, CancellationToken ct) =>
        GetAsync<CombinedStatus>(owner, name, $"{Repo(owner, name)}/commits/{Uri.EscapeDataString(sha)}/status", ct);
    public async Task<IReadOnlyList<ContextStatus>> StatusesAsync(string owner, string name, string sha, CancellationToken ct)
    {
        var statuses = new List<ContextStatus>();
        for (var page = 1; ; page++)
        {
            var batch = await GetAsync<List<ContextStatus>>(owner, name,
                $"{Repo(owner, name)}/commits/{Uri.EscapeDataString(sha)}/statuses?per_page=100&page={page}", ct) ?? [];
            statuses.AddRange(batch);
            if (batch.Count < 100) return statuses;
        }
    }
    public async Task<IReadOnlyList<RepositoryTreeEntry>> TreeAsync(string owner, string name, string sha, CancellationToken ct)
    {
        var tree = await GetAsync<RepositoryTreeResponse>(owner, name, $"{Repo(owner, name)}/git/trees/{Uri.EscapeDataString(sha)}?recursive=1", ct);
        if (tree?.Truncated == true) throw new InvalidOperationException("GitHub returned a truncated tree; context indexing is incomplete");
        return tree?.Tree ?? [];
    }
    public async Task<HttpResponseMessage> ArchiveAsync(string owner, string name, string sha, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Repo(owner, name)}/tarball/{Uri.EscapeDataString(sha)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetInstallationTokenAsync(owner, name, ct));
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        try
        {
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location;
                if (location is not { IsAbsoluteUri: true } || location.Scheme != Uri.UriSchemeHttps || location.Port != 443
                    || !string.Equals(location.Host, "codeload.github.com", StringComparison.OrdinalIgnoreCase)
                    || location.UserInfo.Length != 0 || location.Fragment.Length != 0
                    || !location.AbsolutePath.StartsWith($"/{owner}/{name}/", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("GitHub archive redirect is outside the requested repository boundary");
                response.Dispose();
                using var download = new HttpRequestMessage(HttpMethod.Get, location);
                response = await _http.SendAsync(download, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            EnsureSuccess(response);
            return response;
        }
        catch { response.Dispose(); throw; }
    }
    public async Task<string> FileAsync(string owner, string name, string sha, string path, CancellationToken ct)
    {
        var content = await GetAsync<RepositoryContent>(owner, name, $"{Repo(owner, name)}/contents/{EscapePath(path)}?ref={Uri.EscapeDataString(sha)}", ct)
            ?? throw new InvalidOperationException("GitHub returned no file content");
        if (!string.Equals(content.Type, "file", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(content.Content)) return string.Empty;
        return string.Equals(content.Encoding, "base64", StringComparison.OrdinalIgnoreCase)
            ? Encoding.UTF8.GetString(Convert.FromBase64String(content.Content.Replace("\n", string.Empty, StringComparison.Ordinal)))
            : content.Content;
    }

    private async Task<T?> GetAsync<T>(string owner, string name, string path, CancellationToken ct) =>
        await GetWithTokenAsync<T>(path, await GetInstallationTokenAsync(owner, name, ct), ct);

    private async Task<T?> GetWithTokenAsync<T>(string path, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request, ct);
        EnsureSuccess(response);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
    }

    private async Task SendAsync(string owner, string name, HttpMethod method, string path, object body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body, options: JsonOptions) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetInstallationTokenAsync(owner, name, ct));
        using var response = await _http.SendAsync(request, ct);
        EnsureSuccess(response);
    }

    private Task<string> ActionsTokenAsync(string owner, string name, CancellationToken ct)
    {
        if (!ValidRepository(owner, name)) throw new InvalidOperationException("repository is outside the GitHub installation boundary");
        return InstallationTokenAsync(name, ct, actions: true);
    }

    private async Task<string> InstallationTokenAsync(string name, CancellationToken ct, bool actions = false)
    {
        if (_tokenFactory is not null) return await _tokenFactory(_options.Organization, name, ct);
        var key = actions ? "actions:" + name : name;
        if (_tokens.TryGetValue(key, out var token) && token.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2)) return token.Token;
        await _tokenGate.WaitAsync(ct);
        try
        {
            if (_tokens.TryGetValue(key, out token) && token.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2)) return token.Token;
            var jwt = await CreateAppJwtAsync(ct);
            var installation = await GetWithTokenAsync<AppInstallation>($"app/installations/{_options.GitHubInstallationId}", jwt, ct);
            if (installation?.Account?.Type != "Organization" || !string.Equals(installation.Account.Login, _options.Organization, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("GitHub App installation must belong to the TerraScale organization");
            var permissions = actions
                ? new Dictionary<string, string> { ["actions"] = "write", ["metadata"] = "read" }
                : new Dictionary<string, string> { ["contents"] = "read", ["metadata"] = "read", ["pull_requests"] = "write", ["issues"] = "write", ["statuses"] = "write" };
            using var request = new HttpRequestMessage(HttpMethod.Post, $"app/installations/{_options.GitHubInstallationId}/access_tokens")
            {
                Content = JsonContent.Create(new { repositories = name.Length == 0 ? null : new[] { name }, permissions },
                    options: new JsonSerializerOptions(JsonOptions) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
            using var response = await _http.SendAsync(request, ct);
            EnsureSuccess(response);
            token = await response.Content.ReadFromJsonAsync<InstallationToken>(JsonOptions, ct)
                ?? throw new InvalidOperationException("GitHub returned no installation token");
            if (string.IsNullOrWhiteSpace(token.Token) || token.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(2)
                || (name.Length > 0 && token.Repositories?.Any(x => ValidRepository(x.Owner.Login, x.Name)
                    && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)) != true))
                throw new InvalidOperationException("GitHub token does not include the requested installation repository");
            _tokens[key] = token;
            return token.Token;
        }
        finally { _tokenGate.Release(); }
    }

    private async Task<string> CreateAppJwtAsync(CancellationToken ct)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(await File.ReadAllTextAsync(_options.GitHubPrivateKeyPath, ct));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { iat = now - 60, exp = now + 540, iss = _options.GitHubAppId }));
        var message = header + "." + payload;
        return message + "." + Base64Url(rsa.SignData(Encoding.ASCII.GetBytes(message), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    // Provider error bodies can echo request details; never put credential material in exceptions or logs.
    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"GitHub API returned HTTP {(int)response.StatusCode}", null, response.StatusCode);
    }

    private bool ValidRepository(string owner, string name) =>
        string.Equals(owner, _options.Organization, StringComparison.OrdinalIgnoreCase) && ValidSegment(name);
    private static bool ValidSegment(string value) => value.Length is > 0 and <= 100 && value is not ("." or "..")
        && value.All(x => char.IsAsciiLetterOrDigit(x) || x is '-' or '_' or '.');
    private static string Repo(string owner, string name) => $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}";
    private static string EscapePath(string path) => string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private sealed record CollaboratorPermission([property: JsonPropertyName("permission")] string Permission);
    private sealed record AppInstallation([property: JsonPropertyName("account")] Owner Account);
    private sealed record RepositoryBranch([property: JsonPropertyName("commit")] RepositoryCommit Commit);
    private sealed record RepositoryCommit([property: JsonPropertyName("sha")] string Sha);
    private sealed record InstallationToken([property: JsonPropertyName("token")] string Token,
        [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt,
        [property: JsonPropertyName("repositories")] IReadOnlyList<Repository>? Repositories);
}
