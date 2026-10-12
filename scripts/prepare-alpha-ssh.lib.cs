using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace TerraScale.Deployment;

public static class AlphaSsh
{
    private const string Bao = "https://bao.mariogk.com/v1/";
    private const string Host = "alpha.tail057f.ts.net";

    public static async Task<int> RunAsync()
    {
        try
        {
            if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") != "TerraScale/ts-reviewer"
                || Environment.GetEnvironmentVariable("GITHUB_REF") != "refs/heads/main"
                || Environment.GetEnvironmentVariable("GITHUB_EVENT_NAME") != "push") throw new InvalidOperationException();
            var directory = Path.Combine(Path.GetFullPath(Required("RUNNER_TEMP")), "ts-reviewer-alpha-ssh");
            if (Directory.Exists(directory) || directory.Any(c => char.IsWhiteSpace(c) || c is '"' or '\\')) throw new InvalidOperationException();
            var hostKey = Required("ALPHA_HOST_ED25519_PUBLIC_KEY");
            if (hostKey.Contains('\n') || hostKey.Contains('\r')) throw new InvalidOperationException();
            var parts = hostKey.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[0] != "ssh-ed25519" || Convert.FromBase64String(parts[1]).Length != 51) throw new InvalidOperationException();
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var identity = Path.Combine(directory, "identity");
            var start = new ProcessStartInfo("ssh-keygen") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-q", "-t", "ed25519", "-N", "", "-f", identity }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
            await stdout;
            await stderr;
            if (process.ExitCode != 0) throw new InvalidOperationException();
            File.SetUnixFileMode(identity, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
            var certificate = await SignAsync(http, Required("ACTIONS_ID_TOKEN_REQUEST_URL"), Required("ACTIONS_ID_TOKEN_REQUEST_TOKEN"), await File.ReadAllTextAsync(identity + ".pub"));
            await File.WriteAllTextAsync(identity + "-cert.pub", certificate);
            await File.WriteAllTextAsync(Path.Combine(directory, "known_hosts"), Host + " " + parts[0] + " " + parts[1] + "\n");
            await File.WriteAllTextAsync(Path.Combine(directory, "config"),
                $"Host alpha\n  HostName {Host}\n  User ts-reviewer-deploy\n  IdentityFile {identity}\n  CertificateFile {identity}-cert.pub\n  UserKnownHostsFile {directory}/known_hosts\n  StrictHostKeyChecking yes\n  IdentitiesOnly yes\n  IdentityAgent none\n  BatchMode yes\n");
            Console.WriteLine("Temporary Alpha SSH identity signed through GitHub OIDC; no credential values printed.");
            return 0;
        }
        catch
        {
            Console.Error.WriteLine("Alpha CI identity setup failed. Verify main-only OpenBao OIDC/signing trust, pinned host identity, and scoped Alpha account; details suppressed.");
            return 1;
        }
    }

    public static async Task<string> SignAsync(HttpClient http, string oidcUrl, string oidcToken, string publicKey)
    {
        var uri = new Uri(oidcUrl);
        if (uri.Scheme != "https" || uri.Port != 443 || !uri.Host.EndsWith(".actions.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment)) throw new InvalidOperationException();
        using var identityRequest = new HttpRequestMessage(HttpMethod.Get, oidcUrl + (uri.Query.Length == 0 ? "?" : "&") + "audience=" + Uri.EscapeDataString("https://bao.mariogk.com"));
        identityRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", oidcToken);
        using var identity = await JsonAsync(http, identityRequest);
        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, Bao + "auth/github-actions/login")
        {
            Content = JsonContent.Create(new { role = "ts-reviewer-alpha", jwt = identity.RootElement.GetProperty("value").GetString() })
        };
        using var login = await JsonAsync(http, loginRequest);
        var token = login.RootElement.GetProperty("auth").GetProperty("client_token").GetString();
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException();
        try
        {
            using var signRequest = new HttpRequestMessage(HttpMethod.Post, Bao + "ssh-client-signer/sign/ts-reviewer-alpha")
            {
                Content = JsonContent.Create(new { public_key = publicKey, cert_type = "user", valid_principals = "ts-reviewer-deploy", ttl = "10m" })
            };
            signRequest.Headers.Add("X-Vault-Token", token);
            using var signed = await JsonAsync(http, signRequest);
            var certificate = signed.RootElement.GetProperty("data").GetProperty("signed_key").GetString();
            if (certificate is null || !certificate.StartsWith("ssh-ed25519-cert-v01@openssh.com ", StringComparison.Ordinal)) throw new InvalidOperationException();
            return certificate;
        }
        finally
        {
            using var revoke = new HttpRequestMessage(HttpMethod.Post, Bao + "auth/token/revoke-self");
            revoke.Headers.Add("X-Vault-Token", token);
            using var response = await http.SendAsync(revoke, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException();
        }
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new InvalidOperationException();

    private static async Task<JsonDocument> JsonAsync(HttpClient http, HttpRequestMessage request)
    {
        using var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException();
        await using var body = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(body);
    }
}
