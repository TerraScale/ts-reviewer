using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TerraScale.Deployment;

public static class ReviewerSecrets
{
    private const string AuthorityKey = "TS_REVIEWER_GITHUB_CREDENTIAL_AUTHORITY";
    private const string Authority = "github-app:5279339/installation:170416009";
    private static readonly Dictionary<string, string> PublicValues = new(StringComparer.Ordinal)
    {
        ["TS_REVIEWER_GITHUB_APP_ID"] = "5279339",
        ["TS_REVIEWER_GITHUB_INSTALLATION_ID"] = "170416009",
        ["TS_REVIEWER_BOT_LOGIN"] = "terrascale-ts-reviewer[bot]",
        ["TS_REVIEWER_GITHUB_PRIVATE_KEY_PATH"] = "/secrets/github-app.pem",
        ["TS_REVIEWER_CONTEXT_DATABASE"] = "/var/lib/ts-reviewer/reviewer-context.db",
        [AuthorityKey] = Authority
    };
    private static readonly string[] GeneratedKeys =
        ["TS_REVIEWER_WEBHOOK_SECRET", "TS_REVIEWER_CONTEXT_API_TOKEN", "TS_REVIEWER_OPERATOR_TOKEN"];

    public static async Task<int> RunAsync(string[] args,
        Func<string[], string?, Task<string>>? execute = null, TextWriter? output = null,
        Func<string, string, Task>? updateWebhook = null, Func<string, Task<string>>? discoverWorkspace = null)
    {
        output ??= Console.Out;
        try
        {
            if (args.Length != 1 || args[0] is not ("plan" or "workspace" or "provision" or "webhook"))
                throw new InvalidOperationException();
            execute ??= Execute;
            using var snapshot = JsonDocument.Parse(await execute(
                ["kv", "get", "-address=https://bao.mariogk.com", "-disable-redirects", "-format=json",
                 "-mount=secret/homelab", "ci/ts-reviewer"], null));
            var data = snapshot.RootElement.GetProperty("data");
            var version = data.GetProperty("metadata").GetProperty("version").GetInt32();
            if (version <= 0) throw new InvalidOperationException();
            var values = data.GetProperty("data");
            if (args[0] == "workspace")
            {
                if (!Present(values, "TS_REVIEWER_ANTHROPIC_API_KEY")) throw new InvalidOperationException();
                var workspace = await (discoverWorkspace ?? (key => DiscoverWorkspace(key)))(values.GetProperty("TS_REVIEWER_ANTHROPIC_API_KEY").GetString()!);
                if (!workspace.StartsWith("wrkspc_", StringComparison.Ordinal) || workspace.Length <= 7
                    || !workspace.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')) throw new InvalidOperationException();
                if (Present(values, "TS_REVIEWER_ANTHROPIC_WORKSPACE_ID"))
                {
                    if (values.GetProperty("TS_REVIEWER_ANTHROPIC_WORKSPACE_ID").GetString() != workspace) throw new InvalidOperationException();
                    await output.WriteLineAsync("Anthropic workspace identity already matches the stored inference key; no fields changed.");
                    return 0;
                }
                await execute(["kv", "patch", "-address=https://bao.mariogk.com", "-disable-redirects", "-format=json",
                    "-mount=secret/homelab", "-method=patch", "-cas=" + version, "ci/ts-reviewer", "-"],
                    JsonSerializer.Serialize(new Dictionary<string, string> { ["TS_REVIEWER_ANTHROPIC_WORKSPACE_ID"] = workspace }));
                await output.WriteLineAsync("Anthropic workspace identity discovered through the free token-count endpoint and saved directly to OpenBao; no values printed or vendor credentials changed.");
                return 0;
            }
            // Vendor credentials stay in this process; only new fields are sent in the CAS patch.
            var missing = new List<string>();
            foreach (var key in new[] { "TS_REVIEWER_ANTHROPIC_API_KEY", "TS_REVIEWER_ANTHROPIC_ADMIN_API_KEY",
                "TS_REVIEWER_ANTHROPIC_WORKSPACE_ID", "TS_REVIEWER_GITHUB_PRIVATE_KEY_PEM" })
                if (!Present(values, key)) missing.Add(key);
            if (missing.Count != 0)
            {
                await output.WriteLineAsync("OpenBao /ci/ts-reviewer is missing these secure field names; no writes performed:");
                foreach (var key in missing) await output.WriteLineAsync(key);
                return 1;
            }
            var additions = Prepare(values);
            if (args[0] == "webhook")
            {
                if (additions.Count != 0) throw new InvalidOperationException();
                using var rsa = RSA.Create();
                rsa.ImportFromPem(values.GetProperty("TS_REVIEWER_GITHUB_PRIVATE_KEY_PEM").GetString()!);
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var unsigned = Base64Url(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\"}")) + "." +
                    Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { iss = "5279339", iat = now - 60, exp = now + 540 }));
                var jwt = unsigned + "." + Base64Url(rsa.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
                await (updateWebhook ?? ((token, secret) => UpdateWebhook(token, secret)))(jwt, values.GetProperty("TS_REVIEWER_WEBHOOK_SECRET").GetString()!);
                await output.WriteLineAsync("GitHub App webhook configured directly from OpenBao; no values printed or runtime files changed.");
                return 0;
            }
            if (args[0] == "plan")
            {
                await output.WriteLineAsync("OpenBao /ci/ts-reviewer: vendor credentials configured; pending field names only:");
                foreach (var key in additions.Keys) await output.WriteLineAsync(key);
                return 0;
            }
            if (additions.Count == 0)
            {
                await output.WriteLineAsync("OpenBao reviewer setup is already complete; no fields changed.");
                return 0;
            }
            foreach (var key in GeneratedKeys)
                if (additions.ContainsKey(key)) additions[key] = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            await execute(["kv", "patch", "-address=https://bao.mariogk.com", "-disable-redirects", "-format=json",
                "-mount=secret/homelab", "-method=patch", "-cas=" + version, "ci/ts-reviewer", "-"], JsonSerializer.Serialize(additions));
            await output.WriteLineAsync("OpenBao reviewer setup saved; vendor credentials preserved. Runtime materialization remains separate.");
            return 0;
        }
        catch
        {
            await output.WriteLineAsync("Reviewer secret setup failed without exposing values. Check authenticated OpenBao access, vendor fields, public identities, and CAS version; no automatic retry. Usage: provision-reviewer-secrets.cs plan|workspace|provision|webhook.");
            return 1;
        }
    }

    private static Dictionary<string, string> Prepare(JsonElement values)
    {
        // Provenance distinguishes GitHub credentials from identically named Forgejo-era fields.
        var firstHandoff = !Present(values, AuthorityKey);
        var additions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, expected) in PublicValues)
        {
            if (Present(values, key))
            {
                if (values.GetProperty(key).GetString() != expected) throw new InvalidOperationException();
            }
            else additions[key] = expected;
        }
        foreach (var key in GeneratedKeys)
        {
            var present = Present(values, key);
            if (firstHandoff || !present) additions[key] = string.Empty;
        }
        return additions;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static async Task<string> DiscoverWorkspace(string apiKey, HttpMessageHandler? handler = null)
    {
        using var client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages/count_tokens");
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Content = JsonContent.Create(new { model = "claude-opus-5-5", messages = new[] { new { role = "user", content = "Workspace identity preflight" } } });
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode || !response.Headers.TryGetValues("anthropic-workspace-id", out var ids)) throw new InvalidOperationException();
        var workspace = ids.ToArray();
        if (workspace.Length != 1) throw new InvalidOperationException();
        return workspace[0];
    }

    public static async Task UpdateWebhook(string jwt, string secret, HttpMessageHandler? handler = null)
    {
        using var client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        using var request = new HttpRequestMessage(HttpMethod.Patch, "https://api.github.com/app/hook/config");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        request.Headers.UserAgent.ParseAdd("TerraScale-Reviewer-Setup");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        // GitHub's native webhook field requires the literal "0" to enforce certificate verification.
        request.Content = JsonContent.Create(new { url = "https://ts-reviewer.mariogk.com/webhook", content_type = "json", secret, insecure_ssl = "0" });
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException();
    }

    private static bool Present(JsonElement values, string key)
    {
        if (!values.TryGetProperty(key, out var value)) return false;
        if (value.ValueKind != JsonValueKind.String) throw new InvalidOperationException();
        return !string.IsNullOrWhiteSpace(value.GetString());
    }

    private static async Task<string> Execute(string[] arguments, string? input)
    {
        var start = new ProcessStartInfo("bao")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
        };
        // Native OpenBao TLS verification must remain active for credential-bearing requests.
        start.Environment["BAO_SKIP_VERIFY"] = "false";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException();
        var response = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await errors;
            throw new InvalidOperationException();
        }
        await errors;
        if (process.ExitCode != 0) throw new InvalidOperationException();
        return await response;
    }
}
