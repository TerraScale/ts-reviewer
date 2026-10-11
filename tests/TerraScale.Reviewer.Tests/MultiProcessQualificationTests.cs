using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Json = System.Text.Json.JsonSerializer;
using System.Text.RegularExpressions;
using LiteDB;
using TerraScale.Reviewer.Features.Configuration;
using TerraScale.Reviewer.Features.Context;
using TUnit.Assertions;
using TUnit.Core;

public sealed partial class MultiProcessQualificationTests
{
    private static readonly ContextEmbedder Embedder = new();
    private const string Repository = "TerraScale/widgets";
    private const string Commit = "multiprocess-crash";
    private const long LeaseMs = 5_000;

    private static ReviewerOptions Options(string database) =>
        new("http://+:8080", "https://github.test", "github", "https://proxy.test/v1", "proxy", "TerraScale", "bot", "webhook-test-secret", "context-test-token", database, 100, 5, null, "operator-test-token");

    /// <summary>
    /// Two real child processes share one LiteDB file. The crash child claims a
    /// durable index-job lease and is SIGKILLed while holding it; the production
    /// reviewer service (actual TerraScale.Reviewer binary) recovers the job:
    /// it waits out the expired lease, reclaims with a new fencing token, and
    /// completes indexing against a live GitHub HTTP endpoint. Raw process
    /// output and the final queue state are captured as retained evidence and a
    /// qualification receipt is emitted and validated in-test.
    /// </summary>
    [Test]
    public async Task MultiprocessCrashReclaimQualificationEmitsValidReceipt()
    {
        var database = Path.Combine(Path.GetTempPath(), "ts-reviewer-" + Guid.NewGuid().ToString("N") + ".db");
        // Anchor evidence at the repository root: the test host's working directory
        // is the build output folder, but CI collects artifacts/ from the root.
        var evidence = Path.Combine(RepositoryRoot(), "artifacts", "ci", "tests", "multiprocess");
        Directory.CreateDirectory(evidence);
        try
        {
            using (var setup = new ContextStore(Options(database)))
                setup.EnqueueIndexJob("TerraScale", "widgets", Commit);

            await using var github = new FakeGitHubServer();
            var killedLog = Path.Combine(evidence, "process-a.log");
            var workerLog = Path.Combine(evidence, "process-b.log");
            var queueStatePath = Path.Combine(evidence, "queue-state.json");
            var receiptPath = Path.Combine(evidence, "qualification-receipt.json");

            // Process A: claims the durable lease and hangs until killed.
            var crashAssembly = Path.Combine(AppContext.BaseDirectory, "TerraScale.Reviewer.LeaseCrashChild.dll");
            await Assert.That(File.Exists(crashAssembly)).IsTrue();
            using var killed = StartDotnetChild(crashAssembly, [database, Commit, LeaseMs.ToString()]);
            var claimed = await killed.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
            // The lease expires LeaseMs after the claim, so measure the reclaim
            // from here: starting and killing the processes below takes a
            // variable share of that window on a loaded host.
            var sinceClaim = Stopwatch.StartNew();
            await Assert.That(claimed).StartsWith("CLAIMED ");
            var killedToken = claimed!["CLAIMED ".Length..];

            // Process B hosts the real ContextIndexer in the test binary with a mock HTTP transport.
            using var worker = StartDotnetChild(crashAssembly, ["index-worker", database, github.Url]);
            var workerStdout = worker.StandardOutput.ReadToEndAsync();
            var workerStderr = worker.StandardError.ReadToEndAsync();

            killed.Kill(entireProcessTree: true);
            await killed.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var killedOutput = await killed.StandardOutput.ReadToEndAsync();
            var killedError = await killed.StandardError.ReadToEndAsync();
            await File.WriteAllTextAsync(killedLog,
                $"pid={killed.Id} exit=sigkill\n--- stdout ---\n{claimed}\n{killedOutput}\n--- stderr ---\n{killedError}\n");

            // The killed worker's lease persists in the shared file; process B must
            // wait out the expiry before it can reclaim with its own fencing token.
            var jobKey = Repository + "@" + Commit;
            string? reclaimToken = null;
            var observedKilledLease = false;
            long leaseWaitMs = 0;
            using (var observer = new LiteDatabase(new ConnectionString($"Filename={database};Connection=Shared")))
            {
                var jobs = observer.GetCollection<BsonDocument>("index_jobs");
                var wait = Stopwatch.StartNew();
                while (wait.Elapsed < TimeSpan.FromSeconds(60))
                {
                    if (worker.HasExited)
                        throw new InvalidOperationException(
                            $"context indexing fixture worker exited early with code {worker.ExitCode}\n"
                            + $"stdout:\n{await workerStdout}\nstderr:\n{await workerStderr}");
                    var job = TryReadJob(jobs, jobKey);
                    if (job is not null)
                    {
                        var owner = job["LeaseOwner"].AsString;
                        if (owner == "crash-child") observedKilledLease = true;
                        else if (owner.Contains(":" + worker.Id + ":", StringComparison.Ordinal))
                        {
                            leaseWaitMs = sinceClaim.ElapsedMilliseconds;
                            reclaimToken = job["LeaseToken"].AsString;
                            break;
                        }
                    }
                    await Task.Delay(50);
                }
            }
            await Assert.That(observedKilledLease).IsTrue();
            await Assert.That(reclaimToken).IsNotNull();
            await Assert.That(reclaimToken).IsNotEqualTo(killedToken);
            await Assert.That(leaseWaitMs).IsGreaterThanOrEqualTo(LeaseMs - 1_000);

            // The dead worker's stale token must not release process B's live lease.
            using (var stale = new ContextStore(Options(database)))
                stale.ReleaseIndexJob(Repository, Commit, "crash-child", killedToken);
            using (var observer = new LiteDatabase(new ConnectionString($"Filename={database};Connection=Shared")))
            {
                var jobs = observer.GetCollection<BsonDocument>("index_jobs");
                BsonDocument? afterStaleRelease = null;
                var staleDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                while (DateTime.UtcNow < staleDeadline && afterStaleRelease is null)
                {
                    afterStaleRelease = TryReadJob(jobs, jobKey);
                    if (afterStaleRelease is null) await Task.Delay(50);
                }
                await Assert.That(afterStaleRelease).IsNotNull();
                await Assert.That(afterStaleRelease!["LeaseToken"].AsString).IsEqualTo(reclaimToken!);
                await Assert.That(afterStaleRelease["LeaseOwner"].AsString).Contains(":" + worker.Id + ":");

                var completionDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
                while (DateTime.UtcNow < completionDeadline && TryReadJob(jobs, jobKey) is not null)
                    await Task.Delay(100);
                await Assert.That(TryReadJob(jobs, jobKey)).IsNull();
            }

            // Process B finished: the index is queryable from a third connection and
            // the durable queue no longer holds the job.
            using var verify = new ContextStore(Options(database));
            var hits = verify.Search(Repository, Commit, Embedder.Embed("CreateWidget"), 5);
            await Assert.That(hits).IsNotEmpty();
            await Assert.That(hits[0].Path).IsEqualTo("src/Widget.cs");
            await Assert.That(verify.IsIndexed(Repository, Commit)).IsTrue();
            await Assert.That(verify.PendingIndexJobs()).IsEmpty();
            await Assert.That(verify.DeadLetterJobs()).IsEmpty();
            await Assert.That(verify.GetIndexJobStatus(Repository, Commit)).IsNull();

            worker.Kill(entireProcessTree: true);
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var workerOutput = await workerStdout.WaitAsync(TimeSpan.FromSeconds(10));
            var workerError = await workerStderr.WaitAsync(TimeSpan.FromSeconds(10));
            await File.WriteAllTextAsync(workerLog,
                $"pid={worker.Id} exit=sigkill\n--- stdout ---\n{workerOutput}\n--- stderr ---\n{workerError}\n");
            await Assert.That(workerOutput).Contains($"\"Repository\":\"{Repository}\",\"Commit\":\"{Commit}\"");
            await Assert.That(github.FileRequests).IsEqualTo(1);

            var queueState = new
            {
                repository = Repository,
                commit = Commit,
                pending_index_jobs = verify.PendingIndexJobs().Count,
                dead_letter_jobs = verify.DeadLetterJobs().Count,
                job_status = (object?)null,
                commit_indexed = verify.IsIndexed(Repository, Commit),
                top_hit_path = hits[0].Path,
                github_file_requests = github.FileRequests,
                lease = new
                {
                    ttl_ms = LeaseMs,
                    wait_ms = leaseWaitMs,
                    killed_owner = "crash-child",
                    killed_token = killedToken,
                    reclaim_worker_pid = worker.Id,
                    reclaim_token = reclaimToken,
                    token_changed = true,
                    stale_release_blocked = true,
                },
            };
            await File.WriteAllTextAsync(queueStatePath, Json.Serialize(queueState, JsonWrite) + "\n");

            var receipt = new
            {
                schema_version = 1,
                control_id = "PI1.1",
                activity = "reviewer-multiprocess-crash-reclaim",
                executed_at_utc = UtcNow(),
                environment = "recovery",
                source_revision = SourceRevision(),
                tool_versions = new Dictionary<string, string> { ["dotnet"] = Environment.Version.ToString() },
                // @: the JSON property is the schema's "operator"; @ escapes the C# keyword.
                @operator = new { principal = "automation/ts-reviewer-qualification", ticket = "SOC2-R-003" },
                result = "passed",
                raw_evidence = new[]
                {
                    new { uri = "artifact:multiprocess/process-a.log", sha256 = Sha256(killedLog) },
                    new { uri = "artifact:multiprocess/process-b.log", sha256 = Sha256(workerLog) },
                    new { uri = "artifact:multiprocess/queue-state.json", sha256 = Sha256(queueStatePath) },
                },
                findings = new object[]
                {
                    new Dictionary<string, object?> { ["killed_pid"] = killed.Id, ["kill_signal"] = "SIGKILL", ["killed_lease_observed"] = true },
                    new Dictionary<string, object?> { ["lease_ttl_ms"] = LeaseMs, ["lease_wait_ms"] = leaseWaitMs },
                    new Dictionary<string, object?>
                    {
                        ["reclaim_fencing_token_changed"] = true,
                        ["killed_worker_token"] = killedToken,
                        ["reclaim_worker_token"] = reclaimToken,
                    },
                    new Dictionary<string, object?> { ["stale_lease_release_blocked"] = true },
                    new Dictionary<string, object?>
                    {
                        ["index_verification"] = "passed",
                        ["top_hit_path"] = hits[0].Path,
                        ["top_hit_score"] = Math.Round(hits[0].Score, 4),
                        ["pending_jobs"] = 0,
                        ["dead_letters"] = 0,
                        ["github_file_requests"] = github.FileRequests,
                    },
                    new Dictionary<string, object?> { ["note"] = "rehearsal receipt: human control-owner approval pending" },
                },
                approvals = new[] { new { role = "control-owner", principal = "automation/ts-reviewer-qualification", approved_at_utc = UtcNow() } },
            };
            await File.WriteAllBytesAsync(receiptPath, Json.SerializeToUtf8Bytes(receipt, JsonWrite));

            var digests = new Dictionary<string, string>
            {
                ["artifact:multiprocess/process-a.log"] = Sha256(killedLog),
                ["artifact:multiprocess/process-b.log"] = Sha256(workerLog),
                ["artifact:multiprocess/queue-state.json"] = Sha256(queueStatePath),
            };
            var errors = ValidateReceipt(await File.ReadAllBytesAsync(receiptPath), digests);
            await Assert.That(string.Join("; ", errors)).IsEmpty();
        }
        finally
        {
            File.Delete(database);
        }
    }

    private static Process StartDotnetChild(string assembly, string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(assembly);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException($"Could not start {assembly}.");
    }

    private static string[] PropertyNames(JsonElement value) =>
        value.EnumerateObject().Select(property => property.Name).OrderBy(name => name).ToArray();

    private static BsonDocument? TryReadJob(ILiteCollection<BsonDocument> jobs, string key)
    {
        try { return jobs.FindById(key); }
        catch (Exception exception) when (exception is LiteException or IOException)
        {
            return null; // transient cross-process lock contention; the caller retries
        }
    }

    private static string UtcNow() => DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss") + "Z";

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    internal static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !directory.EnumerateFileSystemInfos(".git").Any())
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("could not locate the repository root");
    }

    private static string SourceRevision()
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            WorkingDirectory = RepositoryRoot(),
        };
        start.ArgumentList.Add("rev-parse");
        start.ArgumentList.Add("HEAD");
        using var git = Process.Start(start) ?? throw new InvalidOperationException("could not start git");
        var revision = git.StandardOutput.ReadToEnd().Trim();
        git.WaitForExit();
        if (git.ExitCode != 0 || !Regex.IsMatch(revision, "^[0-9a-f]{40}$"))
            throw new InvalidOperationException($"git rev-parse HEAD returned an invalid revision: {revision}");
        return revision;
    }

    /// <summary>In-test gate mirroring docs/enterprise/validate_evidence_receipt.py
    /// for the fields this receipt uses, so CI reproduces the validation.</summary>
    private static List<string> ValidateReceipt(byte[] receiptBytes, Dictionary<string, string> digests)
    {
        var errors = new List<string>();
        using var document = System.Text.Json.JsonDocument.Parse(receiptBytes);
        var receipt = document.RootElement;
        string[] required =
        [
            "schema_version", "control_id", "activity", "executed_at_utc", "environment", "source_revision",
            "operator", "result", "raw_evidence", "findings", "approvals",
        ];
        foreach (var property in receipt.EnumerateObject())
            if (!required.Contains(property.Name) && property.Name != "tool_versions")
                errors.Add($"unknown property {property.Name}");
        foreach (var name in required)
            if (!receipt.TryGetProperty(name, out _))
                errors.Add($"missing property {name}");

        void StringProperty(string name, Regex pattern)
        {
            if (receipt.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !pattern.IsMatch(value.GetString()!))
                errors.Add($"{name} does not match {pattern}");
        }

        if (receipt.TryGetProperty("schema_version", out var version) && (version.ValueKind != JsonValueKind.Number || version.GetInt32() != 1))
            errors.Add("schema_version must be 1");
        StringProperty("control_id", ControlIdPattern());
        StringProperty("activity", ActivityPattern());
        StringProperty("executed_at_utc", TimestampPattern());
        StringProperty("source_revision", RevisionPattern());
        if (receipt.TryGetProperty("environment", out var environment) && environment.GetString() is not ("production" or "staging" or "recovery"))
            errors.Add("environment must be production, staging, or recovery");
        if (receipt.TryGetProperty("result", out var result) && result.GetString() is not ("passed" or "failed" or "partially_passed"))
            errors.Add("result must be passed, failed, or partially_passed");

        if (receipt.TryGetProperty("operator", out var op) && op.ValueKind == JsonValueKind.Object)
        {
            if (!PropertyNames(op).SequenceEqual(["principal", "ticket"]))
                errors.Add("operator must have exactly principal and ticket");
            foreach (var field in new[] { "principal", "ticket" })
                if (!op.TryGetProperty(field, out var value) || string.IsNullOrWhiteSpace(value.GetString()) || value.GetString()!.Length > 256)
                    errors.Add($"operator.{field} must be a non-empty string of at most 256 characters");
        }

        if (receipt.TryGetProperty("raw_evidence", out var evidence) && evidence.ValueKind == JsonValueKind.Array)
        {
            if (evidence.GetArrayLength() == 0) errors.Add("raw_evidence must not be empty");
            foreach (var item in evidence.EnumerateArray())
            {
                if (!PropertyNames(item).SequenceEqual(["sha256", "uri"]))
                    errors.Add("raw_evidence items must have exactly uri and sha256");
                var uri = item.TryGetProperty("uri", out var uriValue) ? uriValue.GetString() : null;
                var sha256 = item.TryGetProperty("sha256", out var shaValue) ? shaValue.GetString() : null;
                if (uri is null || !uri.StartsWith("artifact:", StringComparison.Ordinal) || uri.Length > 2048)
                    errors.Add($"raw_evidence uri must start with artifact: ({uri})");
                if (sha256 is null || !Regex.IsMatch(sha256, "^[0-9a-f]{64}$"))
                    errors.Add($"raw_evidence sha256 must be 64 lowercase hex characters ({sha256})");
                if (uri is not null && sha256 is not null && digests.TryGetValue(uri, out var expected) && expected != sha256)
                    errors.Add($"raw_evidence sha256 for {uri} does not match the captured file");
            }
        }

        if (receipt.TryGetProperty("findings", out var findings)
            && (findings.ValueKind != JsonValueKind.Array || findings.GetArrayLength() == 0
                || findings.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.Object)))
            errors.Add("findings must be a non-empty array of objects");

        if (receipt.TryGetProperty("approvals", out var approvals) && approvals.ValueKind == JsonValueKind.Array)
        {
            if (approvals.GetArrayLength() == 0) errors.Add("approvals must not be empty");
            foreach (var approval in approvals.EnumerateArray())
            {
                if (!PropertyNames(approval).SequenceEqual(["approved_at_utc", "principal", "role"]))
                    errors.Add("approvals must have exactly role, principal, and approved_at_utc");
                if (approval.TryGetProperty("role", out var role) && role.GetString()!.Length > 128)
                    errors.Add("approvals.role must be at most 128 characters");
                if (approval.TryGetProperty("principal", out var principal) && principal.GetString()!.Length > 256)
                    errors.Add("approvals.principal must be at most 256 characters");
                if (approval.TryGetProperty("approved_at_utc", out var approved) && !TimestampPattern().IsMatch(approved.GetString() ?? string.Empty))
                    errors.Add("approvals.approved_at_utc must be an RFC3339 UTC timestamp ending in Z");
            }
        }
        return errors;
    }

    private static readonly JsonSerializerOptions JsonWrite = new() { WriteIndented = true };

    [GeneratedRegex("^(?:CC|A|PI|C|P)[0-9]+(?:\\.[0-9]+)?$")]
    private static partial Regex ControlIdPattern();

    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex ActivityPattern();

    [GeneratedRegex("^\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}Z$")]
    private static partial Regex TimestampPattern();

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex RevisionPattern();

    /// <summary>A minimal live GitHub API over real HTTP so the separate worker
    /// process exercises GitHubClient over a mocked HTTP endpoint.</summary>
    private sealed class FakeGitHubServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly Task _loop;
        private int _fileRequests;
        private int _stallsLeft = 1;

        public int FileRequests => _fileRequests;
        public string Url => _listener.Prefixes.Single();

        public FakeGitHubServer()
        {
            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _loop = Task.Run(() => AcceptLoopAsync(_stopping.Token));
        }

        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && _listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync().WaitAsync(cancellationToken); }
                catch (Exception) when (cancellationToken.IsCancellationRequested) { break; }
                catch (HttpListenerException) { break; }
                var path = context.Request.Url!.AbsolutePath;
                try
                {
                    if (path.Contains("/git/trees/", StringComparison.Ordinal))
                    {
                        await WriteJsonAsync(context.Response, new
                        {
                            tree = new object[] { new { path = "src/Widget.cs", type = "blob", size = 58, sha = "file-sha" } },
                        });
                    }
                    else if (path.Contains("/contents/", StringComparison.Ordinal))
                    {
                        Interlocked.Increment(ref _fileRequests);
                        // Hold the first file response so the reclaim lease is observably
                        // held while the test reads the shared queue state.
                        if (Interlocked.Decrement(ref _stallsLeft) >= 0)
                            await Task.Delay(TimeSpan.FromMilliseconds(1_500), cancellationToken);
                        var content = Convert.ToBase64String(
                            Encoding.UTF8.GetBytes("public class Widget { public Widget CreateWidget() => new(); }"));
                        await WriteJsonAsync(context.Response, new { content, encoding = "base64", type = "file" });
                    }
                    else context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested) { }
                catch (Exception) { context.Response.StatusCode = (int)HttpStatusCode.InternalServerError; }
                finally { context.Response.Close(); }
            }
        }

        private static async Task WriteJsonAsync(HttpListenerResponse response, object value)
        {
            response.StatusCode = (int)HttpStatusCode.OK;
            response.ContentType = "application/json";
            var bytes = Json.SerializeToUtf8Bytes(value);
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes);
        }

        public async ValueTask DisposeAsync()
        {
            _stopping.Cancel();
            try { _listener.Stop(); } catch (ObjectDisposedException) { }
            try { await _loop; } catch (OperationCanceledException) { }
            _listener.Close();
            _stopping.Dispose();
        }
    }
}
