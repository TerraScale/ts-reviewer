using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraScale.Reviewer.Features.Configuration;
using TerraScale.Reviewer.Features.GitHub;
using TUnit.Assertions;
using TUnit.Core;

public sealed class GitHubWebhookConfigurationTests
{
    [Test]
    [Arguments(200)]
    [Arguments(500)]
    [Arguments(0)]
    public async Task ConfiguresFixedWebhookWithAppJwtWithoutInstallationTokenOrRetry(int status)
    {
        using var rsa = RSA.Create(2048);
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, rsa.ExportRSAPrivateKeyPem());
        var calls = 0;
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            calls++;
            await Assert.That(ct.CanBeCanceled).IsTrue();
            await Assert.That(request.Method).IsEqualTo(HttpMethod.Patch);
            await Assert.That(request.RequestUri!.AbsoluteUri).IsEqualTo("https://api.github.com/app/hook/config");
            await Assert.That(request.Headers.Authorization!.Scheme).IsEqualTo("Bearer");
            await Assert.That(request.Headers.Accept.Single().MediaType).IsEqualTo("application/vnd.github+json");
            await Assert.That(request.Headers.GetValues("X-GitHub-Api-Version").Single()).IsEqualTo("2022-11-28");
            await Assert.That(request.Headers.UserAgent.ToString()).IsEqualTo("TerraScale-ts-reviewer/1.0");
            var parts = request.Headers.Authorization.Parameter!.Split('.');
            await Assert.That(rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Decode(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)).IsTrue();
            using var claims = JsonDocument.Parse(Decode(parts[1]));
            await Assert.That(claims.RootElement.GetProperty("iss").GetString()).IsEqualTo("123");
            await Assert.That(claims.RootElement.GetProperty("exp").GetInt64() - claims.RootElement.GetProperty("iat").GetInt64()).IsEqualTo(600L);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            await Assert.That(request.Content.Headers.ContentType!.MediaType).IsEqualTo("application/json");
            await Assert.That(body.RootElement.EnumerateObject().Count()).IsEqualTo(4);
            await Assert.That(body.RootElement.GetProperty("url").GetString()).IsEqualTo("https://ts-reviewer.mariogk.com/webhook");
            await Assert.That(body.RootElement.GetProperty("content_type").GetString()).IsEqualTo("json");
            await Assert.That(body.RootElement.GetProperty("secret").GetString()).IsEqualTo("vault-webhook-secret");
            await Assert.That(body.RootElement.GetProperty("insecure_ssl").GetString()).IsEqualTo("0");
            if (status == 0) throw new HttpRequestException("vault-webhook-secret");
            return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("vault-webhook-secret") };
        }));
        var options = new ReviewerOptions("http://localhost:0", "https://api.github.com", "123", "https://glm.test", "fixture",
            "TerraScale", "bot", "vault-webhook-secret", "context", "unused", 100, GitHubPrivateKeyPath: path);
        var client = new GitHubClient(http, options, (_, _, _) => throw new InvalidOperationException("installation token must not be used"));
        try
        {
            HttpRequestException? failure = null;
            try { await client.ConfigureWebhookAsync(CancellationToken.None); }
            catch (HttpRequestException ex) { failure = ex; }
            await Assert.That(calls).IsEqualTo(1);
            if (status == 200) await Assert.That(failure).IsNull();
            else
            {
                await Assert.That(failure!.Message).IsEqualTo(status == 0 ? "GitHub webhook configuration request failed" : "GitHub API returned HTTP 500");
                await Assert.That(failure.InnerException).IsNull();
            }
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var cancelledBeforeSend = false;
            try { await client.ConfigureWebhookAsync(cancelled.Token); }
            catch (OperationCanceledException) { cancelledBeforeSend = true; }
            await Assert.That(cancelledBeforeSend).IsTrue();
            await Assert.That(calls).IsEqualTo(1);
        }
        finally { File.Delete(path); }
    }

    private static byte[] Decode(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/').PadRight((value.Length + 3) / 4 * 4, '='));
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
