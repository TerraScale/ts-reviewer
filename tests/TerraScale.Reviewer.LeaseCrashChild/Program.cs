using TerraScale.Reviewer.Features.Configuration;
using TerraScale.Reviewer.Features.Context;

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
    "http://localhost:0", "https://forgejo.test", "forgejo", "https://proxy.test/v1", "proxy",
    "TerraScale", "bot", TimeSpan.FromMinutes(1), "webhook", "context", args[0], 10);
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
