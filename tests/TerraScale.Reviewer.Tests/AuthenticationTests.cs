using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using TerraScale.Reviewer.Features.Configuration;
using TerraScale.Reviewer.Features.Context;
using TerraScale.Reviewer.Features.Webhooks;
using TUnit.Assertions;
using TUnit.Core;

public sealed class AuthenticationTests
{
    private static ReviewerOptions Options => new("http://+:8080", "https://forgejo.test", "forgejo",
        "https://proxy.test", "proxy", "review", "TerraScale", "bot", 2, TimeSpan.FromMinutes(1),
        "webhook-test-secret", "context-test-token", "unused", 100);

    [Test]
    [Arguments("webhook-secret", "")]
    [Arguments("webhook-secret", "   ")]
    [Arguments("context-api-token", "")]
    [Arguments("context-api-token", "   ")]
    public async Task ConfigurationRejectsMissingInboundCredentials(string key, string value)
    {
        string? error = null;
        try
        {
            ReviewerOptions.Load(["--forgejo-token=forgejo", "--proxy-token=proxy",
                "--webhook-secret=webhook-test-secret", "--context-api-token=context-test-token", $"--{key}={value}"]);
        }
        catch (InvalidOperationException exception) { error = exception.Message; }
        var environmentName = "TS_REVIEWER_" + key.Replace('-', '_').ToUpperInvariant();
        await Assert.That(error).IsEqualTo(environmentName + " is required");
    }

    [Test]
    [Arguments("", "")]
    [Arguments("", "Bearer ")]
    [Arguments("   ", "Bearer    ")]
    [Arguments("context-test-token", "")]
    [Arguments("context-test-token", "Bearer wrong")]
    [Arguments("context-test-token", "Bearer context-test-token,Bearer context-test-token")]
    public async Task ContextSearchRejectsUnauthorizedBeforeReadingBody(string token, string authorization)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = authorization;
        // Invalid JSON must never be parsed for an unauthorized request.
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream("invalid JSON"u8.ToArray());
        var result = await ContextEndpoints.SearchAsync(context.Request, null!,
            Options with { ContextApiToken = token }, CancellationToken.None);
        await Assert.That(((IStatusCodeHttpResult)result).StatusCode).IsEqualTo(StatusCodes.Status401Unauthorized);
    }

    [Test]
    public async Task AuthenticatedContextSearchReachesRequestValidation()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer context-test-token";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream("null"u8.ToArray());
        var result = await ContextEndpoints.SearchAsync(context.Request, null!, Options, CancellationToken.None);
        await Assert.That(((IStatusCodeHttpResult)result).StatusCode).IsEqualTo(StatusCodes.Status400BadRequest);
    }

    [Test]
    [Arguments("webhook-test-secret", "")]
    [Arguments("webhook-test-secret", "bad")]
    [Arguments("", "empty-key-signature")]
    [Arguments("   ", "empty-key-signature")]
    public async Task WebhookRejectsUnauthorizedBeforeProcessing(string secret, string signature)
    {
        var handler = new WebhookHandler(null!, null!, null!, Options with { WebhookSecret = secret },
            NullLogger<WebhookHandler>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        var body = "invalid JSON"u8.ToArray();
        context.Request.Body = new MemoryStream(body);
        context.Request.Headers["X-Forgejo-Signature"] = signature == "empty-key-signature"
            ? Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body))
            : signature;
        var result = await handler.HandleAsync(context.Request, CancellationToken.None);
        await Assert.That(((IStatusCodeHttpResult)result).StatusCode).IsEqualTo(StatusCodes.Status401Unauthorized);
    }
}
