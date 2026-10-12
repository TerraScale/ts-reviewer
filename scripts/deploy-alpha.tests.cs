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
    public async Task A_paused_or_Forgejo_owned_deployer_prevents_handoff(string value)
    {
        using var policy = JsonDocument.Parse(value);
        await Assert.That(() => AlphaDeployment.ValidateHandoff(policy.RootElement)).Throws<InvalidOperationException>();
    }

    [Test]
    public void Handoff_requires_no_new_deployment_switch()
    {
        using var policy = JsonDocument.Parse("{\"apps\":{\"other-app\":{}}}");
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
