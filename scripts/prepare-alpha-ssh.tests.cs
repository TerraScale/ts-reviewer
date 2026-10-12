#!/usr/bin/env dotnet run
#:property ManagePackageVersionsCentrally=false
#:property PublishAot=false
#:package TUnit@1.72.10
#:include prepare-alpha-ssh.lib.cs

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TerraScale.Deployment;

public sealed class AlphaSshTests
{
    [Test]
    [Arguments("success")]
    [Arguments("identity_denied")]
    [Arguments("login_denied")]
    [Arguments("sign_denied")]
    [Arguments("sign_and_revoke_denied")]
    [Arguments("sign_redirect")]
    [Arguments("wrong_certificate")]
    [Arguments("revoke_denied")]
    public async Task Oidc_can_only_sign_short_lived_reviewer_identity_and_always_revokes_its_token(string behavior)
    {
        var calls = new List<string>();
        using var http = new HttpClient(new Handler(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            calls.Add(path);
            switch (path)
            {
                case "/idtoken":
                    await Assert.That(request.RequestUri.Host).IsEqualTo("pipelines.actions.githubusercontent.com");
                    await Assert.That(Uri.UnescapeDataString(request.RequestUri.Query)).IsEqualTo("?audience=https://bao.mariogk.com");
                    await Assert.That(request.Headers.Authorization!.Parameter).IsEqualTo("synthetic-runner-token");
                    return new(behavior == "identity_denied" ? HttpStatusCode.Forbidden : HttpStatusCode.OK)
                        { Content = JsonContent.Create(new { value = "synthetic-oidc-jwt" }) };
                case "/v1/auth/github-actions/login":
                    await Assert.That(request.RequestUri.Host).IsEqualTo("bao.mariogk.com");
                    await Assert.That(request.Headers.Authorization).IsNull();
                    using (var login = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()))
                    {
                        await Assert.That(login.RootElement.GetProperty("role").GetString()).IsEqualTo("ts-reviewer-alpha");
                        await Assert.That(login.RootElement.GetProperty("jwt").GetString()).IsEqualTo("synthetic-oidc-jwt");
                    }
                    return new(behavior == "login_denied" ? HttpStatusCode.Forbidden : HttpStatusCode.OK)
                        { Content = JsonContent.Create(new { auth = new { client_token = "synthetic-bao-token" } }) };
                case "/v1/ssh-client-signer/sign/ts-reviewer-alpha":
                    await Assert.That(request.RequestUri.Host).IsEqualTo("bao.mariogk.com");
                    await Assert.That(request.Headers.GetValues("X-Vault-Token").Single()).IsEqualTo("synthetic-bao-token");
                    using (var sign = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()))
                    {
                        await Assert.That(sign.RootElement.GetProperty("public_key").GetString()).IsEqualTo("synthetic-ephemeral-public-key");
                        await Assert.That(sign.RootElement.GetProperty("valid_principals").GetString()).IsEqualTo("ts-reviewer-deploy");
                        await Assert.That(sign.RootElement.GetProperty("cert_type").GetString()).IsEqualTo("user");
                        await Assert.That(sign.RootElement.GetProperty("ttl").GetString()).IsEqualTo("10m");
                    }
                    var response = new HttpResponseMessage(behavior switch
                    {
                        "sign_denied" or "sign_and_revoke_denied" => HttpStatusCode.Forbidden,
                        "sign_redirect" => HttpStatusCode.Redirect,
                        _ => HttpStatusCode.OK
                    }) { Content = JsonContent.Create(new { data = new { signed_key = behavior == "wrong_certificate" ? "synthetic-provider-secret" : "ssh-ed25519-cert-v01@openssh.com synthetic-certificate" } }) };
                    response.Headers.Location = new Uri("https://untrusted.example.invalid/");
                    return response;
                case "/v1/auth/token/revoke-self":
                    await Assert.That(request.Headers.GetValues("X-Vault-Token").Single()).IsEqualTo("synthetic-bao-token");
                    return new(behavior is "revoke_denied" or "sign_and_revoke_denied" ? HttpStatusCode.Forbidden : HttpStatusCode.NoContent);
                default: throw new InvalidOperationException("Unexpected credential destination.");
            }
        }));
        if (behavior == "success")
            await Assert.That(await AlphaSsh.SignAsync(http, "https://pipelines.actions.githubusercontent.com/idtoken", "synthetic-runner-token", "synthetic-ephemeral-public-key"))
                .IsEqualTo("ssh-ed25519-cert-v01@openssh.com synthetic-certificate");
        else
        {
            var failure = await Assert.That(async () => { await AlphaSsh.SignAsync(http, "https://pipelines.actions.githubusercontent.com/idtoken", "synthetic-runner-token", "synthetic-ephemeral-public-key"); }).Throws<InvalidOperationException>();
            await Assert.That(TUnit.Core.TestContext.Current!.GetErrorOutput()).IsEqualTo(behavior == "sign_and_revoke_denied"
                ? "OpenBao token revocation HTTP 403 also failed; details suppressed." + Environment.NewLine : "");
            var message = failure!.Message;
            await Assert.That(message.Contains("synthetic", StringComparison.Ordinal)).IsFalse();
            if (behavior != "wrong_certificate")
                await Assert.That(message).IsEqualTo(behavior switch
                {
                    "identity_denied" => "GitHub OIDC request HTTP 403",
                    "login_denied" => "OpenBao authentication HTTP 403",
                    "revoke_denied" => "OpenBao token revocation HTTP 403",
                    "sign_redirect" => "OpenBao SSH signing HTTP 302",
                    _ => "OpenBao SSH signing HTTP 403"
                });
        }
        await Assert.That(calls.Count).IsEqualTo(behavior == "identity_denied" ? 1 : behavior == "login_denied" ? 2 : 4);
        if (behavior is not ("login_denied" or "identity_denied")) await Assert.That(calls.Last()).IsEqualTo("/v1/auth/token/revoke-self");
    }

    [Test]
    [Arguments("http://pipelines.actions.githubusercontent.com/idtoken")]
    [Arguments("https://pipelines.actions.githubusercontent.com.untrusted.example/idtoken")]
    [Arguments("https://untrusted.example/idtoken")]
    [Arguments("https://user@pipelines.actions.githubusercontent.com/idtoken")]
    [Arguments("https://pipelines.actions.githubusercontent.com:8443/idtoken")]
    public async Task Runner_token_never_leaves_GitHub_oidc_origin(string url)
    {
        var requests = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            requests++;
            throw new InvalidOperationException();
        }));
        await Assert.That(async () => { await AlphaSsh.SignAsync(http, url, "synthetic-runner-token", "synthetic-public-key"); }).Throws<InvalidOperationException>();
        await Assert.That(requests).IsEqualTo(0);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
