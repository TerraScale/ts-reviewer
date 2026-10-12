using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace TerraScale.Deployment;

public static partial class AlphaDeployment
{
    private const string Repository = "registry.mariogk.com/homelab/ts-reviewer";
    private const string RegistryAuth = "/root/.config/containers/auth.json";
    private const string StatePath = "/var/lib/ts-reviewer-deploy/state.json";
    private const string SecretsPath = "/var/srv/appdata/ts-reviewer/ts-reviewer.env";
    private const string PemPath = "/var/srv/appdata/ts-reviewer/github-app.pem";
    private const string InitialRollbackEnv = "/var/srv/appdata/ts-reviewer/rollback/ts-reviewer.env";

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsLinux() || args.Length != 3)
                throw new InvalidOperationException("Usage: deploy-alpha <full-commit-sha> <image-digest> <artifact-directory> (Alpha Linux root only)");
            var (commit, digest, directory) = (args[0], args[1], Path.GetFullPath(args[2]));
            ValidateIdentity(commit, digest);
            using var deployLock = File.OpenHandle("/run/lock/homelab-deployer.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite);
            if (Flock(deployLock, 2 | 4) != 0)
                throw new InvalidOperationException("Another HomeLab deployment holds the global deployment lock; retry the GitHub job.");

            using var policy = JsonDocument.Parse(await File.ReadAllTextAsync("/opt/homelab/deployer/apps.json"));
            ValidateHandoff(policy.RootElement);
            await ValidateInstanceAsync(Command);
            await ValidateSecretFilesAsync(Command);
            var archive = Path.Combine(directory, "image.oci.tar");
            using var image = JsonDocument.Parse(await Command("skopeo", "inspect", "oci-archive:" + archive));
            ValidateImage(image.RootElement, commit, digest);
            await ValidateConfigurationAsync(directory, Command);

            var state = File.Exists(StatePath)
                ? JsonSerializer.Deserialize<DeploymentState>(await File.ReadAllTextAsync(StatePath))
                    ?? throw new InvalidOperationException("Deployment state is invalid.")
                : null;
            if (state is { Phase: not "healthy" })
                throw new InvalidOperationException("An interrupted deployment needs recovery using docs/deployment.md before another roll.");
            var rollbackEnv = state is null ? InitialRollbackEnv : StatePath + ".rollback.env";
            if (state is null)
            {
                if (!File.Exists(rollbackEnv) || (File.GetUnixFileMode(rollbackEnv)
                    & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite)) != 0)
                    throw new InvalidOperationException("The approved first-migration rollback dotenv snapshot must exist with root-only permissions before deployment.");
            }
            if (state is { Commit: var deployed } && deployed == commit)
            {
                if (state.Digest != digest) throw new InvalidOperationException("The deployed commit has a different immutable image digest.");
                if ((await Command("skopeo", "inspect", "--authfile", RegistryAuth, "--format", "{{.Digest}}", "docker://" + Repository + ":latest")).Trim() != digest)
                    throw new InvalidOperationException("The recorded image differs from Harbor latest; operator recovery is required.");
                if (!await Healthy()) throw new InvalidOperationException("The current deployment is unhealthy; recover it before rerunning.");
                Console.WriteLine("Alpha already runs this exact healthy commit and digest.");
                return 0;
            }

            var previous = (await Command("skopeo", "inspect", "--authfile", RegistryAuth, "--format", "{{.Digest}}", "docker://" + Repository + ":latest")).Trim();
            if (!DigestPattern().IsMatch(previous)) throw new InvalidOperationException("The current image cannot be resolved for rollback.");
            var rollbackTag = "rollback-" + previous[7..];
            await Command("skopeo", "copy", "--quiet", "--preserve-digests", "--src-authfile", RegistryAuth,
                "--dest-authfile", RegistryAuth, "docker://" + Repository + "@" + previous, "docker://" + Repository + ":" + rollbackTag);
            await Command("skopeo", "copy", "--quiet", "--preserve-digests", "--dest-authfile", RegistryAuth,
                "oci-archive:" + archive, "docker://" + Repository + ":" + commit);
            if ((await Command("skopeo", "inspect", "--authfile", RegistryAuth, "--format", "{{.Digest}}", "docker://" + Repository + ":" + commit)).Trim() != digest)
                throw new InvalidOperationException("The staged Harbor image digest does not match the tested image.");

            if (state is not null) await SecureCopy(SecretsPath, rollbackEnv);
            state = new(commit, digest, rollbackTag, rollbackEnv, "prepared");
            await Save(state);
            await RollAsync(commit, rollbackTag, async tag =>
                {
                    if (tag == rollbackTag)
                        await RestoreSecretsAsync(rollbackEnv, Command);
                    await RebuildAsync(tag, Command);
                }, Healthy,
                imageDigest => Command("skopeo", "copy", "--quiet", "--preserve-digests", "--src-authfile", RegistryAuth,
                    "--dest-authfile", RegistryAuth, "docker://" + Repository + "@" + imageDigest, "docker://" + Repository + ":latest"),
                phase => Save(state with { Phase = phase }), digest, previous);
            Console.WriteLine("Alpha runs the exact tested commit and image digest; health check passed.");
            return 0;
        }
        catch (Exception exception)
        {
            // Child-process output and exception details can contain credentials; print only our fixed diagnostic.
            Console.Error.WriteLine(exception is InvalidOperationException ? exception.Message : "Deployment preflight failed; inspect the documented prerequisites without printing secrets.");
            return 1;
        }
    }

    public static void ValidateIdentity(string commit, string digest)
    {
        if (!CommitPattern().IsMatch(commit) || !DigestPattern().IsMatch(digest))
            throw new InvalidOperationException("Deployment requires a full lowercase commit SHA and sha256 image digest.");
    }

    public static void ValidateHandoff(JsonElement policy)
    {
        if (policy.TryGetProperty("enabled", out var enabled) && enabled.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException("HomeLab deployment is paused; an approved GitHub delivery migration must resolve the existing policy first.");
        if (!policy.TryGetProperty("apps", out var apps) || apps.ValueKind != JsonValueKind.Object
            || apps.TryGetProperty("ts-reviewer", out _))
            throw new InvalidOperationException("HomeLab must transfer ts-reviewer image ownership from its Forgejo poller to this GitHub pipeline first.");
        if (!policy.TryGetProperty("external_image_rolls", out var external) || !external.TryGetProperty("ts-reviewer", out var owner)
            || owner.GetProperty("repo").GetString() != "TerraScale/ts-reviewer"
            || owner.GetProperty("branch").GetString() != "main"
            || owner.GetProperty("workflow").GetString() != ".github/workflows/ci.yml"
            || owner.GetProperty("harbor").GetString() != "homelab/ts-reviewer"
            || owner.GetProperty("instance").GetString() != "ts-reviewer")
            throw new InvalidOperationException("HomeLab must explicitly declare the main-only ts-reviewer GitHub image-roll owner before deployment.");
    }

    public static void ValidateInstance(JsonElement instance)
    {
        var config = instance.GetProperty("expanded_config");
        var entrypoint = config.GetProperty("oci.entrypoint").GetString() ?? "";
        if (!entrypoint.Contains(". /secrets/ts-reviewer.env", StringComparison.Ordinal)
            || !(entrypoint.Contains("exec /app/TerraScale.Reviewer", StringComparison.Ordinal)
                || entrypoint.Contains("exec /ts-reviewer", StringComparison.Ordinal)))
            throw new InvalidOperationException("The Incus entrypoint must source the runtime secret file and execute the reviewer binary.");
        var devices = instance.GetProperty("expanded_devices");
        foreach (var (source, target) in new[] { (SecretsPath, "/secrets/ts-reviewer.env"), (PemPath, "/secrets/github-app.pem") })
        {
            if (!devices.EnumerateObject().Any(device =>
                    device.Value.TryGetProperty("type", out var type) && type.GetString() == "disk"
                    && device.Value.TryGetProperty("source", out var from) && from.GetString() == source
                    && device.Value.TryGetProperty("path", out var to) && to.GetString() == target
                    && device.Value.TryGetProperty("readonly", out var readOnly) && readOnly.GetString() == "true"
                    && device.Value.TryGetProperty("shift", out var shift) && shift.GetString() == "true"))
                throw new InvalidOperationException("The approved Incus secret-file and GitHub PEM mounts must both be present, read-only, and shifted for the runtime user.");
        }
    }

    public static async Task ValidateInstanceAsync(Func<string, string[], Task<string>> command)
    {
        using var instance = JsonDocument.Parse(await command("incus", ["query", "/1.0/instances/ts-reviewer"]));
        ValidateInstance(instance.RootElement);
    }

    public static async Task ValidateSecretFilesAsync(Func<string, string[], Task<string>> command)
    {
        var metadata = await command("stat", ["-c", "%a:%u:%g", "--", SecretsPath, PemPath]);
        if (!metadata.Split('\n', StringSplitOptions.RemoveEmptyEntries).SequenceEqual(new[] { "600:1654:1654", "600:1654:1654" }))
            throw new InvalidOperationException("Both runtime secret files must have mode 0600 and uid/gid 1654 before deployment.");
    }

    public static Task<string> ValidateConfigurationAsync(string directory, Func<string, string[], Task<string>> command) =>
        // Reviewed application artifacts must never execute as host root during preflight.
        command("setpriv", ["--reuid=1654", "--regid=1654", "--clear-groups", "--no-new-privs", "sh", "-c",
            "set -a; . /var/srv/appdata/ts-reviewer/ts-reviewer.env; set +a; " +
            "test \"$TS_REVIEWER_GITHUB_PRIVATE_KEY_PATH\" = /secrets/github-app.pem || exit 1; " +
            "export TS_REVIEWER_GITHUB_PRIVATE_KEY_PATH=/var/srv/appdata/ts-reviewer/github-app.pem; " +
            "exec \"$1\" --validate-configuration", "reviewer-preflight", Path.Combine(directory, "reviewer", "TerraScale.Reviewer")]);

    public static Task<string> RestoreSecretsAsync(string source, Func<string, string[], Task<string>> command) =>
        command("install", ["-m", "0600", "-o", "1654", "-g", "1654", "--", source, SecretsPath]);

    public static void ValidateImage(JsonElement image, string commit, string digest)
    {
        if (image.GetProperty("Digest").GetString() != digest
            || image.GetProperty("Labels").GetProperty("org.opencontainers.image.revision").GetString() != commit)
            throw new InvalidOperationException("The OCI archive does not match the trusted tested commit and immutable image digest.");
    }

    private static async Task<string> Command(string program, params string[] arguments)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("A required deployment tool could not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new InvalidOperationException("A deployment command exceeded its 15-minute limit.");
        }
        await errors;
        if (process.ExitCode != 0) throw new InvalidOperationException($"{program} deployment command failed; its output is withheld to protect credentials.");
        return await output;
    }

    public static async Task RebuildAsync(string tag, Func<string, string[], Task<string>> command)
    {
        using var state = JsonDocument.Parse(await command("incus", ["query", "/1.0/instances/ts-reviewer/state"]));
        if (state.RootElement.GetProperty("status").GetString() == "Running")
        {
            try { await command("incus", ["stop", "ts-reviewer", "--timeout", "60"]); }
            catch (InvalidOperationException) { await command("incus", ["stop", "ts-reviewer", "--force"]); }
        }
        await command("incus", ["rebuild", "harbor:homelab/ts-reviewer:" + tag, "ts-reviewer"]);
        await command("incus", ["start", "ts-reviewer"]);
    }

    public static async Task RollAsync(string commit, string rollbackTag, Func<string, Task> rebuild,
        Func<Task<bool>> healthy, Func<string, Task> promote, Func<string, Task> record, string nextDigest, string previousDigest)
    {
        try
        {
            await record("rebuilding");
            await rebuild(commit);
            if (!await healthy()) throw new InvalidOperationException("The replacement did not become healthy within 120 seconds.");
            await promote(nextDigest);
            await record("healthy");
        }
        catch
        {
            await record("rolling-back");
            await rebuild(rollbackTag);
            if (!await healthy()) throw new InvalidOperationException("Rollback is unhealthy; operator recovery is required.");
            await promote(previousDigest);
            await record("rolled-back");
            throw new InvalidOperationException("The replacement failed; Alpha is healthy on the previous immutable image. Review the failed deployment before retrying.");
        }
    }

    private static async Task<bool> Healthy()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var deadline = Stopwatch.StartNew();
        do
        {
            try
            {
                using var response = await client.GetAsync("http://127.0.0.1:3018/healthz");
                if (response.StatusCode == HttpStatusCode.OK) return true;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException) { }
            await Task.Delay(TimeSpan.FromSeconds(3));
        } while (deadline.Elapsed < TimeSpan.FromSeconds(120));
        return false;
    }

    private static async Task Save(DeploymentState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        var temporary = StatePath + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write))
        {
            await JsonSerializer.SerializeAsync(stream, state);
            stream.Flush(flushToDisk: true);
        }
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, StatePath, overwrite: true);
    }

    private static async Task SecureCopy(string source, string target)
    {
        if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("Deployment secret snapshots require Alpha Linux.");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await using var input = File.OpenRead(source);
        await using var output = new FileStream(target, new FileStreamOptions
        {
            Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        });
        await input.CopyToAsync(output);
        output.Flush(flushToDisk: true);
    }

    private sealed record DeploymentState(string Commit, string Digest, string RollbackTag, string RollbackEnv, string Phase);

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(SafeFileHandle handle, int operation);

    [GeneratedRegex("\\A[0-9a-f]{40}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex CommitPattern();
    [GeneratedRegex("\\Asha256:[0-9a-f]{64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex DigestPattern();
}
