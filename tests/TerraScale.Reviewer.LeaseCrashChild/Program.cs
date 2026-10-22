using TerraScale.Reviewer.Features.Configuration;
using TerraScale.Reviewer.Features.Context;
using TerraScale.Reviewer.Features.GitHub;
using Microsoft.Extensions.Logging;

// The transport seam belongs only to this test binary; production always authenticates with the App.
if (args is ["index-worker", var database, var endpoint])
{
    var fixtureOptions = new ReviewerOptions("http://localhost:0", endpoint, "1", "https://api.z.ai/api/anthropic", "fixture",
        "TerraScale", "bot", "webhook", "context", database, 100);
    using var fixtureStore = new ContextStore(fixtureOptions);
    using var loggerFactory = LoggerFactory.Create(logging => logging.AddJsonConsole());
    var github = new GitHubClient(new HttpClient(), fixtureOptions, (_, _, _) => Task.FromResult("installation-test-token"));
    using var worker = new ContextIndexer(github, fixtureStore, new ContextEmbedder(), fixtureOptions,
        loggerFactory.CreateLogger<ContextIndexer>());
    await worker.StartAsync(CancellationToken.None);
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 0;
}

if (args.Length is < 1 or > 3 || string.IsNullOrWhiteSpace(args[0]))
{
    Console.Error.WriteLine("usage: TerraScale.Reviewer.LeaseCrashChild <database> [commit] [lease-ms]");
    return 2;
}

var commit = args.Length >= 2 && !string.IsNullOrWhiteSpace(args[1]) ? args[1] : "process-crash";
var leaseLifetime = args.Length == 3 && long.TryParse(args[2], out var parsed) && parsed > 0
    ? TimeSpan.FromMilliseconds(parsed)
    : TimeSpan.FromMilliseconds(100);

var options = new ReviewerOptions(
    "http://localhost:0", "https://github.test", "1", "https://api.z.ai/api/anthropic", "fixture",
    "TerraScale", "bot", "webhook", "context", args[0], 10);
using var store = new ContextStore(options);
var token = store.TryClaimIndexJob("TerraScale/widgets", commit, "crash-child", leaseLifetime);
if (token is null)
{
    Console.Error.WriteLine("job was not claimable");
    return 3;
}

Console.WriteLine($"CLAIMED {token}");
Console.Out.Flush();
await Task.Delay(Timeout.InfiniteTimeSpan);
return 0;
