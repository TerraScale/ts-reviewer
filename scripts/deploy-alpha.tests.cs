#!/usr/bin/env dotnet run
#:property ManagePackageVersionsCentrally=false
#:property PublishAot=false
#:package TUnit@1.72.10
#:include deploy-alpha.lib.cs

using System.Text.Json;
using TerraScale.Deployment;

public sealed class DeploymentTests
{
    private const string Commit = "abcdef0123456789012345678901234567890123";
    private const string Digest = "sha256:0123456789012345678901234567890123456789012345678901234567890123";

    [Test]
    public async Task Full_commit_and_image_digest_are_required()
    {
        AlphaDeployment.ValidateIdentity(Commit, Digest);
        foreach (var (commit, digest) in new[] { ("main", Digest), (Commit, "latest"), (Commit.ToUpperInvariant(), Digest), (Commit + ";echo", Digest) })
            await Assert.That(() => AlphaDeployment.ValidateIdentity(commit, digest)).Throws<InvalidOperationException>();
    }

    [Test]
    [Arguments("{\"enabled\":false,\"apps\":{}}")]
    [Arguments("{\"apps\":{\"ts-reviewer\":{}}}")]
    [Arguments("{}")]
    [Arguments("{\"apps\":{}}")]
    [Arguments("{\"apps\":{},\"external_image_rolls\":{\"ts-reviewer\":{\"repo\":\"untrusted/repo\"}}}")]
    public async Task A_paused_or_Forgejo_owned_deployer_prevents_handoff(string value)
    {
        using var policy = JsonDocument.Parse(value);
        await Assert.That(() => AlphaDeployment.ValidateHandoff(policy.RootElement)).Throws<InvalidOperationException>();
    }

    [Test]
    public void Handoff_requires_no_new_deployment_switch()
    {
        using var policy = JsonDocument.Parse("""
            {"apps":{"other-app":{}},"external_image_rolls":{"ts-reviewer":{
              "repo":"TerraScale/ts-reviewer","branch":"main","workflow":".github/workflows/ci.yml",
              "harbor":"homelab/ts-reviewer","instance":"ts-reviewer"}}}
            """);
        AlphaDeployment.ValidateHandoff(policy.RootElement);
    }

    [Test]
    public async Task Archive_digest_and_commit_label_must_both_match()
    {
        using var image = JsonDocument.Parse("{\"Digest\":\"" + Digest + "\",\"Labels\":{\"org.opencontainers.image.revision\":\"" + Commit + "\"}}");
        AlphaDeployment.ValidateImage(image.RootElement, Commit, Digest);
        await Assert.That(() => AlphaDeployment.ValidateImage(image.RootElement, new string('a', 40), Digest)).Throws<InvalidOperationException>();
        await Assert.That(() => AlphaDeployment.ValidateImage(image.RootElement, Commit, "sha256:" + new string('a', 64))).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Incus_JSON_query_validates_expanded_shifted_readonly_mounts_before_service_stop()
    {
        const string valid = """
            {"config":{},"devices":{},
             "expanded_config":{"oci.entrypoint":"sh -c 'set -a; . /secrets/ts-reviewer.env; set +a; exec /app/TerraScale.Reviewer'"},
             "expanded_devices":{"env":{"type":"disk","source":"/var/srv/appdata/ts-reviewer/ts-reviewer.env","path":"/secrets/ts-reviewer.env","readonly":"true","shift":"true"},
                                 "pem":{"type":"disk","source":"/var/srv/appdata/ts-reviewer/github-app.pem","path":"/secrets/github-app.pem","readonly":"true","shift":"true"}}}
            """;
        Task<string> Query(string program, string[] arguments)
        {
            if (program != "incus" || !arguments.SequenceEqual(new[] { "query", "/1.0/instances/ts-reviewer" }))
                throw new InvalidOperationException("Only the read-only Incus JSON API may run during instance validation.");
            return Task.FromResult(valid);
        }
        await AlphaDeployment.ValidateInstanceAsync(Query);
        foreach (var broken in new[]
        {
            valid.Replace("\"readonly\":\"true\"", "\"readonly\":\"false\""),
            valid.Replace("\"shift\":\"true\"", "\"shift\":\"false\""),
            valid.Replace("exec /app/TerraScale.Reviewer", "exec /unrelated-service")
        })
        {
            using var rejected = JsonDocument.Parse(broken);
            await Assert.That(() => AlphaDeployment.ValidateInstance(rejected.RootElement)).Throws<InvalidOperationException>();
        }
    }

    [Test]
    public async Task Secret_file_metadata_is_checked_without_reading_values_before_deployment()
    {
        var metadata = "600:1654:1654\n600:1654:1654\n";
        Task<string> Stat(string program, string[] arguments)
        {
            if (program != "stat" || !arguments.SequenceEqual(new[] { "-c", "%a:%u:%g", "--",
                "/var/srv/appdata/ts-reviewer/ts-reviewer.env", "/var/srv/appdata/ts-reviewer/github-app.pem" }))
                throw new InvalidOperationException("Preflight may inspect secret-file metadata only.");
            return Task.FromResult(metadata);
        }
        await AlphaDeployment.ValidateSecretFilesAsync(Stat);
        foreach (var unsafeMetadata in new[] { "644:1654:1654\n600:1654:1654\n", "600:0:0\n600:1654:1654\n", "600:1654:1654\n" })
        {
            metadata = unsafeMetadata;
            await Assert.That(() => AlphaDeployment.ValidateSecretFilesAsync(Stat)).Throws<InvalidOperationException>();
        }
    }

    [Test]
    public async Task Application_preflight_drops_host_root_and_cannot_gain_privileges()
    {
        var calls = 0;
        await AlphaDeployment.ValidateConfigurationAsync("/tmp/checked-artifact", (program, arguments) =>
        {
            if (program != "setpriv" || !arguments.Take(6).SequenceEqual(new[]
                { "--reuid=1654", "--regid=1654", "--clear-groups", "--no-new-privs", "sh", "-c" })
                || arguments[^1] != "/tmp/checked-artifact/reviewer/TerraScale.Reviewer"
                || !arguments[^3].Contains("--validate-configuration", StringComparison.Ordinal))
                throw new InvalidOperationException("Application preflight must execute only as the runtime user, with configuration passed by path.");
            calls++;
            return Task.FromResult("");
        });
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task Rollback_restores_runtime_owner_only_permissions_before_rebuilding()
    {
        var restored = false;
        Task<string> Install(string program, string[] arguments)
        {
            if (program != "install" || !arguments.SequenceEqual(new[] { "-m", "0600", "-o", "1654", "-g", "1654", "--",
                "/root/test-rollback.env", "/var/srv/appdata/ts-reviewer/ts-reviewer.env" }))
                throw new InvalidOperationException("Rollback must restore owner-only runtime permissions.");
            restored = true;
            return Task.FromResult("");
        }
        await Assert.That(() => AlphaDeployment.RollAsync(Commit, "rollback-exact-digest", async tag =>
            {
                if (tag == Commit) throw new InvalidOperationException("Replacement failed.");
                await AlphaDeployment.RestoreSecretsAsync("/root/test-rollback.env", Install);
            }, () => Task.FromResult(restored), _ => Task.CompletedTask, _ => Task.CompletedTask,
            Digest, "previous-digest")).Throws<InvalidOperationException>();
        await Assert.That(restored).IsTrue();
    }

    [Test]
    public async Task Failed_rebuild_rolls_back_an_already_stopped_instance()
    {
        var running = true;
        var commands = new List<string>();
        var promotions = new List<string>();
        var phases = new List<string>();
        Task<string> Command(string program, string[] arguments)
        {
            commands.Add(string.Join(' ', arguments));
            if (arguments[0] == "query") return Task.FromResult(running ? "{\"status\":\"Running\"}" : "{\"status\":\"Stopped\"}");
            if (arguments[0] == "stop")
            {
                if (!running) throw new InvalidOperationException("Stop called on a stopped instance.");
                running = false;
            }
            if (arguments[0] == "rebuild" && arguments[1].EndsWith(Commit, StringComparison.Ordinal))
                throw new InvalidOperationException("The replacement image cannot rebuild.");
            if (arguments[0] == "start") running = true;
            return Task.FromResult("");
        }
        await Assert.That(() => AlphaDeployment.RollAsync(Commit, "rollback-exact-digest",
            tag => AlphaDeployment.RebuildAsync(tag, Command), () => Task.FromResult(running),
            digest => { promotions.Add(digest); return Task.CompletedTask; },
            phase => { phases.Add(phase); return Task.CompletedTask; }, Digest, "previous-digest")).Throws<InvalidOperationException>();
        await Assert.That(running).IsTrue();
        await Assert.That(commands.Count(command => command.StartsWith("stop ", StringComparison.Ordinal))).IsEqualTo(1);
        await Assert.That(commands.Any(command => command.Contains("rollback-exact-digest", StringComparison.Ordinal))).IsTrue();
        await Assert.That(string.Join(',', promotions)).IsEqualTo("previous-digest");
        await Assert.That(phases.Last()).IsEqualTo("rolled-back");
    }

    [Test]
    public async Task An_unhealthy_replacement_never_promotes_its_digest()
    {
        var rebuilt = new List<string>();
        var promoted = new List<string>();
        await Assert.That(() => AlphaDeployment.RollAsync(Commit, "rollback-exact-digest",
            tag => { rebuilt.Add(tag); return Task.CompletedTask; }, () => Task.FromResult(rebuilt.Last() == "rollback-exact-digest"),
            digest => { promoted.Add(digest); return Task.CompletedTask; }, _ => Task.CompletedTask,
            Digest, "previous-digest")).Throws<InvalidOperationException>();
        await Assert.That(rebuilt.Last()).IsEqualTo("rollback-exact-digest");
        await Assert.That(string.Join(',', promoted)).IsEqualTo("previous-digest");
    }
}
