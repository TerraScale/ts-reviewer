using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using TerraScale.Reviewer.Features.Configuration;
using TerraScale.Reviewer.Features.Context;
using TerraScale.Reviewer.Features.Webhooks;
using TUnit.Assertions;
using TUnit.Core;

[NotInParallel]
public sealed class AuthenticationTests
{
    private static ReviewerOptions Options => new("http://+:8080", "https://github.test", "123",
        "https://anthropic.test", "anthropic-test-key", "TerraScale", "bot",
        "webhook-test-secret", "context-test-token", "unused", 100);
    private static readonly string[] SecretKeys = ["webhook-secret", "context-api-token", "operator-token"];
    private static readonly string[] Credentials = ["--github-app-id=123", "--github-installation-id=456", "--github-private-key-path=unused",
        "--anthropic-api-key=anthropic-test-key", "--anthropic-admin-api-key=billing-test-key", "--anthropic-workspace-id=wrkspc_test", "--bot-login=bot", "--operator-token=operator-test-token",
        "--webhook-secret=webhook-test-secret", "--context-api-token=context-test-token"];

    [Test]
    [Arguments("webhook-secret", "")]
    [Arguments("webhook-secret", "   ")]
    [Arguments("context-api-token", "")]
    [Arguments("context-api-token", "   ")]
    [Arguments("anthropic-admin-api-key", "")]
    [Arguments("anthropic-workspace-id", "")]
    [Arguments("operator-token", "")]
    public async Task ConfigurationRejectsMissingInboundCredentials(string key, string value)
    {
        using var fixture = new ConfigurationFixture();
        string? error = null;
        try
        {
            ReviewerOptions.Load([.. Credentials, $"--{key}={value}"]);
        }
        catch (InvalidOperationException exception) { error = exception.Message; }
        var environmentName = "TS_REVIEWER_" + key.Replace('-', '_').ToUpperInvariant();
        await Assert.That(error).IsEqualTo(environmentName + " is required");
    }

    [Test]
    [Arguments("config")]
    [Arguments("environment")]
    [Arguments("arguments")]
    public async Task FileSecretsFollowPrecedenceAndTrimOnlyTerminalNewlines(string source)
    {
        using var fixture = new ConfigurationFixture();
        File.WriteAllText(fixture.SecretPath, " synthetic\ncontent \r\n");
        var config = SecretKeys.ToDictionary(key => key + "-file", _ => source == "config" ? fixture.SecretPath : "missing");
        File.WriteAllText(fixture.ConfigPath, JsonSerializer.Serialize(config));
        foreach (var key in SecretKeys)
            if (source != "config") Environment.SetEnvironmentVariable(EnvironmentName(key + "-file"), source == "environment" ? fixture.SecretPath : "missing");
        var args = Credentials.Where(arg => !SecretKeys.Any(key => arg.StartsWith("--" + key + "=", StringComparison.Ordinal))).ToList();
        args.Add("--config=" + fixture.ConfigPath);
        if (source == "arguments") args.AddRange(SecretKeys.Select(key => "--" + key + "-file=" + fixture.SecretPath));
        var options = ReviewerOptions.Load([.. args]);
        foreach (var value in new[] { options.WebhookSecret, options.ContextApiToken, options.OperatorToken })
            await Assert.That(value).IsEqualTo(" synthetic\ncontent ");
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments(" \t\r\n")]
    [Arguments("directory")]
    public async Task FileSecretsRejectMissingBlankOrUnreadableFilesWithoutPathDetails(string? content)
    {
        using var fixture = new ConfigurationFixture();
        if (content is null) File.Delete(fixture.SecretPath);
        else File.WriteAllText(fixture.SecretPath, content);
        var path = content == "directory" ? Path.GetTempPath() : fixture.SecretPath;
        string? error = null;
        try { ReviewerOptions.Load([.. Credentials.Where(arg => !arg.StartsWith("--webhook-secret=", StringComparison.Ordinal)), "--webhook-secret-file=" + path]); }
        catch (InvalidOperationException exception)
        {
            error = exception.Message;
            await Assert.That(exception.InnerException).IsNull();
        }
        await Assert.That(error).IsEqualTo("webhook-secret-file must name a readable file containing a nonblank secret");
    }

    [Test]
    [Arguments("config", "environment")]
    [Arguments("environment", "config")]
    [Arguments("arguments", "environment")]
    [Arguments("config", "arguments")]
    [Arguments("arguments", "arguments")]
    public async Task FileSecretsRejectPlainValuesAcrossSourcesEvenWithOverrides(string plainSource, string fileSource)
    {
        using var fixture = new ConfigurationFixture();
        foreach (var key in SecretKeys)
        {
            var config = new Dictionary<string, string>();
            var args = Credentials.Where(arg => !arg.StartsWith("--" + key + "=", StringComparison.Ordinal)).ToList();
            foreach (var (source, name, value) in new[] { (plainSource, key, "synthetic"), (fileSource, key + "-file", fixture.SecretPath) })
            {
                if (source == "config") config[name] = value;
                else if (source == "environment") Environment.SetEnvironmentVariable(EnvironmentName(name), value);
                else args.Add("--" + name + "=" + value);
            }
            File.WriteAllText(fixture.ConfigPath, JsonSerializer.Serialize(config));
            string? error = null;
            try { ReviewerOptions.Load([.. args, "--config=" + fixture.ConfigPath, "--" + key + "-file=override-missing"]); }
            catch (InvalidOperationException exception) { error = exception.Message; }
            await Assert.That(error).IsEqualTo($"{key} and {key}-file cannot both be supplied");
            Environment.SetEnvironmentVariable(EnvironmentName(key), null);
            Environment.SetEnvironmentVariable(EnvironmentName(key + "-file"), null);
        }
    }

    [Test]
    [Arguments("default")]
    [Arguments("wrkspc_")]
    [Arguments("incorrect")]
    [Arguments("wrkspc_bad/id")]
    public async Task ConfigurationRequiresRealWorkspaceIdentityIncludingDefault(string workspace)
    {
        await Assert.That(() => ReviewerOptions.Load(["--github-app-id=123", "--github-installation-id=456", "--github-private-key-path=unused",
            "--anthropic-api-key=anthropic-test-key", "--anthropic-admin-api-key=billing-test-key", "--anthropic-workspace-id=" + workspace,
            "--bot-login=bot", "--operator-token=operator-test-token", "--webhook-secret=webhook-test-secret", "--context-api-token=context-test-token"]))
            .Throws<InvalidOperationException>();
    }

    private static string EnvironmentName(string key) => "TS_REVIEWER_" + key.Replace('-', '_').ToUpperInvariant();
    private sealed class ConfigurationFixture : IDisposable
    {
        private readonly Dictionary<string, string?> _previous = Names().ToDictionary(name => name, Environment.GetEnvironmentVariable);
        public string SecretPath { get; } = Path.GetTempFileName();
        public string ConfigPath { get; } = Path.GetTempFileName();
        public ConfigurationFixture() { foreach (var name in _previous.Keys) Environment.SetEnvironmentVariable(name, null); }
        private static IEnumerable<string> Names() => Environment.GetEnvironmentVariables().Keys.Cast<string>()
            .Where(name => name.StartsWith("TS_REVIEWER_", StringComparison.OrdinalIgnoreCase));
        public void Dispose()
        {
            foreach (var name in Names()) Environment.SetEnvironmentVariable(name, null);
            foreach (var (name, value) in _previous) Environment.SetEnvironmentVariable(name, value);
            File.Delete(SecretPath);
            File.Delete(ConfigPath);
        }
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
        var handler = new WebhookHandler(null!, null!, null!, null!, Options with { WebhookSecret = secret },
            NullLogger<WebhookHandler>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        var body = "invalid JSON"u8.ToArray();
        context.Request.Body = new MemoryStream(body);
        context.Request.Headers["X-Hub-Signature-256"] = signature == "empty-key-signature"
            ? "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body))
            : signature;
        var result = await handler.HandleAsync(context.Request, CancellationToken.None);
        await Assert.That(((IStatusCodeHttpResult)result).StatusCode).IsEqualTo(StatusCodes.Status401Unauthorized);
    }
}
