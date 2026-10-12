#!/usr/bin/env dotnet run
#:property ManagePackageVersionsCentrally=false
#:property PublishAot=false
#:package TUnit@1.72.10
#:include provision-reviewer-secrets.lib.cs

using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using TerraScale.Deployment;

public sealed class ReviewerSecretSetupTests
{
    private const string Vendor = "synthetic-vendor-fixture";
    private static string Snapshot(Dictionary<string, string> values) => JsonSerializer.Serialize(
        new { data = new { data = values, metadata = new { version = 7 } } });
    private static Dictionary<string, string> VendorValues() => new()
    {
        ["TS_REVIEWER_ANTHROPIC_API_KEY"] = Vendor,
        ["TS_REVIEWER_GITHUB_PRIVATE_KEY_PEM"] = "synthetic-pem-fixture"
    };

    [Test]
    public async Task Plan_only_reports_names_and_never_writes_or_generates_credentials()
    {
        using var output = new StringWriter();
        var calls = 0;
        var exit = await ReviewerSecrets.RunAsync(["plan"], (args, input) =>
        {
            if (args[1] != "get" || input is not null) throw new Exception(Vendor);
            calls++;
            return Task.FromResult(Snapshot(VendorValues()));
        }, output);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(output.ToString().Contains(Vendor)).IsFalse();
        await Assert.That(output.ToString().Contains("TS_REVIEWER_WEBHOOK_SECRET")).IsTrue();
    }

    [Test]
    public async Task First_handoff_replaces_legacy_credentials_and_CAS_reruns_preserve_GitHub_generation()
    {
        var values = VendorValues();
        values["TS_REVIEWER_OPERATOR_TOKEN"] = "existing-operator-fixture";
        values["TS_REVIEWER_WEBHOOK_SECRET"] = "existing-forgejo-fixture";
        values["TS_REVIEWER_GITHUB_APP_ID"] = "5279339";
        using var output = new StringWriter();
        var patches = 0;
        var tokens = new List<string>();
        Task<string> Execute(string[] args, string? input)
        {
            if (args[1] == "get") return Task.FromResult(Snapshot(values));
            if (args[1] != "patch" || !args.Contains("-cas=7") || !args.Contains("-method=patch")
                || input is null || args.Any(a => a.Contains(Vendor))) throw new Exception(Vendor);
            using var patch = JsonDocument.Parse(input);
            if (patch.RootElement.TryGetProperty("TS_REVIEWER_ANTHROPIC_API_KEY", out _)
                || patch.RootElement.TryGetProperty("TS_REVIEWER_GITHUB_PRIVATE_KEY_PEM", out _)) throw new Exception(Vendor);
            foreach (var field in patch.RootElement.EnumerateObject())
            {
                values[field.Name] = field.Value.GetString()!;
                if (field.Name is "TS_REVIEWER_WEBHOOK_SECRET" or "TS_REVIEWER_CONTEXT_API_TOKEN" or "TS_REVIEWER_OPERATOR_TOKEN") tokens.Add(field.Value.GetString()!);
            }
            patches++;
            return Task.FromResult("{}");
        }
        await Assert.That(await ReviewerSecrets.RunAsync(["provision"], Execute, output)).IsEqualTo(0);
        await Assert.That(await ReviewerSecrets.RunAsync(["provision"], Execute, output)).IsEqualTo(0);
        await Assert.That(patches).IsEqualTo(1);
        await Assert.That(tokens.Count).IsEqualTo(3);
        await Assert.That(tokens.Distinct().Count()).IsEqualTo(3);
        await Assert.That(tokens.All(t => t.Length == 64 && t.All(Uri.IsHexDigit))).IsTrue();
        await Assert.That(values["TS_REVIEWER_GITHUB_INSTALLATION_ID"]).IsEqualTo("170416009");
        await Assert.That(output.ToString().Contains(Vendor) || tokens.Any(t => output.ToString().Contains(t))).IsFalse();
    }

    [Test]
    public async Task Webhook_setup_uses_signed_App_JWT_and_store_secret_without_store_writes_or_value_output()
    {
        using var rsa = RSA.Create(2048);
        var values = VendorValues();
        values["TS_REVIEWER_GITHUB_PRIVATE_KEY_PEM"] = rsa.ExportRSAPrivateKeyPem();
        using var output = new StringWriter();
        Task<string> Execute(string[] args, string? input)
        {
            if (args[1] == "get") return Task.FromResult(Snapshot(values));
            using var patch = JsonDocument.Parse(input!);
            foreach (var field in patch.RootElement.EnumerateObject()) values[field.Name] = field.Value.GetString()!;
            return Task.FromResult("{}");
        }
        await Assert.That(await ReviewerSecrets.RunAsync(["provision"], Execute, output)).IsEqualTo(0);
        var requests = 0;
        var calls = 0;
        var result = await ReviewerSecrets.RunAsync(["webhook"], (args, input) =>
        {
            calls++;
            if (args[1] != "get" || input is not null) throw new Exception(Vendor);
            return Task.FromResult(Snapshot(values));
        }, output, async (jwt, secret) =>
        {
            requests++;
            var parts = jwt.Split('.');
            static byte[] Decode(string s) => Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/').PadRight((s.Length + 3) / 4 * 4, '='));
            using var payload = JsonDocument.Parse(Decode(parts[1]));
            await Assert.That(payload.RootElement.GetProperty("iss").GetString()).IsEqualTo("5279339");
            await Assert.That(payload.RootElement.GetProperty("exp").GetInt64() - DateTimeOffset.UtcNow.ToUnixTimeSeconds()).IsBetween(500, 540);
            await Assert.That(rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Decode(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)).IsTrue();
            await Assert.That(secret).IsEqualTo(values["TS_REVIEWER_WEBHOOK_SECRET"]);
        });
        await Assert.That(result).IsEqualTo(0);
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(requests).IsEqualTo(1);
        await Assert.That(output.ToString().Contains(values["TS_REVIEWER_WEBHOOK_SECRET"]) || output.ToString().Contains("PRIVATE KEY")).IsFalse();
    }

    [Test]
    public async Task Webhook_setup_refuses_unmigrated_credentials_without_HTTP_or_store_writes()
    {
        using var output = new StringWriter();
        var calls = 0;
        var requests = 0;
        var result = await ReviewerSecrets.RunAsync(["webhook"], (args, input) =>
        {
            calls++;
            if (args[1] != "get" || input is not null) throw new Exception(Vendor);
            return Task.FromResult(Snapshot(VendorValues()));
        }, output, (_, _) => { requests++; return Task.CompletedTask; });
        await Assert.That(result).IsEqualTo(1);
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(requests).IsEqualTo(0);
    }

    [Test]
    [Arguments(200)]
    [Arguments(302)]
    public async Task Webhook_PATCH_targets_only_GitHub_with_TLS_delivery_and_no_redirect_or_retry(int status)
    {
        var calls = 0;
        using var handler = new WebhookHandler(async request =>
        {
            calls++;
            await Assert.That(request.Method).IsEqualTo(HttpMethod.Patch);
            await Assert.That(request.RequestUri!.AbsoluteUri).IsEqualTo("https://api.github.com/app/hook/config");
            await Assert.That(request.Headers.Authorization!.Scheme).IsEqualTo("Bearer");
            await Assert.That(request.Headers.Authorization.Parameter).IsEqualTo("synthetic-jwt-fixture");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            await Assert.That(body.RootElement.GetProperty("url").GetString()).IsEqualTo("https://ts-reviewer.mariogk.com/webhook");
            await Assert.That(body.RootElement.GetProperty("secret").GetString()).IsEqualTo("synthetic-hmac-fixture");
            await Assert.That(body.RootElement.GetProperty("insecure_ssl").GetString()).IsEqualTo("0");
            var response = new HttpResponseMessage((System.Net.HttpStatusCode)status);
            response.Headers.Location = new Uri("https://untrusted.example.invalid/");
            return response;
        });
        if (status == 200) await ReviewerSecrets.UpdateWebhook("synthetic-jwt-fixture", "synthetic-hmac-fixture", handler);
        else await Assert.That(() => ReviewerSecrets.UpdateWebhook("synthetic-jwt-fixture", "synthetic-hmac-fixture", handler)).Throws<InvalidOperationException>();
        await Assert.That(calls).IsEqualTo(1);
    }

    private sealed class WebhookHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }

    [Test]
    [Arguments("vendor")]
    [Arguments("identity")]
    [Arguments("identity-type")]
    [Arguments("cas")]
    public async Task Missing_vendor_fields_wrong_identity_or_CAS_failure_never_leak_or_retry(string failure)
    {
        var values = VendorValues();
        if (failure == "vendor") values.Remove("TS_REVIEWER_ANTHROPIC_API_KEY");
        if (failure is "identity" or "identity-type") values["TS_REVIEWER_GITHUB_APP_ID"] = "1";
        using var output = new StringWriter();
        var patches = 0;
        var exit = await ReviewerSecrets.RunAsync(["provision"], (args, _) =>
        {
            if (args[1] == "get") return Task.FromResult(failure == "identity-type" ? Snapshot(values).Replace("\"1\"", "1") : Snapshot(values));
            patches++;
            throw new Exception(Vendor);
        }, output);
        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(patches).IsEqualTo(failure == "cas" ? 1 : 0);
        await Assert.That(output.ToString().Contains(Vendor)).IsFalse();
    }
}
