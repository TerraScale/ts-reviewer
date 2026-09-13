namespace TerraScale.TsReviewer.Features.Configuration;

public sealed record ReviewerOptions(
    string ListenUrl, string ForgejoUrl, string ForgejoToken, string ProxyUrl, string ProxyToken,
    string Model, string Organization, string BotLogin, int MaxConcurrent, TimeSpan ReviewTimeout,
    long MaxDiffBytes, string WebhookSecret)
{
    public static ReviewerOptions Load(string[] args)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["listen"] = Environment.GetEnvironmentVariable("TS_REVIEWER_LISTEN") ?? "http://+:8080",
            ["forgejo-url"] = Environment.GetEnvironmentVariable("TS_REVIEWER_FORGEJO_URL") ?? "https://git.terrascale.tech",
            ["forgejo-token"] = Environment.GetEnvironmentVariable("TS_REVIEWER_FORGEJO_TOKEN"),
            ["proxy-url"] = Environment.GetEnvironmentVariable("TS_REVIEWER_PROXY_URL") ?? "http://10.11.99.1:8317/v1",
            ["proxy-token"] = Environment.GetEnvironmentVariable("TS_REVIEWER_PROXY_TOKEN"),
            ["model"] = Environment.GetEnvironmentVariable("TS_REVIEWER_MODEL") ?? "glm-5.3-flash",
            ["organization"] = Environment.GetEnvironmentVariable("TS_REVIEWER_ORGANIZATION") ?? "TerraScale",
            ["bot-login"] = Environment.GetEnvironmentVariable("TS_REVIEWER_BOT_LOGIN") ?? "terrascale-bot",
            ["max-concurrent"] = Environment.GetEnvironmentVariable("TS_REVIEWER_MAX_CONCURRENT") ?? "10",
            ["timeout"] = Environment.GetEnvironmentVariable("TS_REVIEWER_TIMEOUT") ?? "00:10:00",
            ["max-diff-bytes"] = Environment.GetEnvironmentVariable("TS_REVIEWER_MAX_DIFF_BYTES") ?? (8L * 1024 * 1024).ToString(),
            ["webhook-secret"] = Environment.GetEnvironmentVariable("TS_REVIEWER_WEBHOOK_SECRET") ?? string.Empty
        };
        foreach (var arg in args)
        {
            if (!arg.StartsWith("--", StringComparison.Ordinal) || !arg.Contains('=')) continue;
            var pair = arg[2..].Split('=', 2);
            values[pair[0]] = pair[1];
        }
        var forgejoToken = Required(values, "forgejo-token", "TS_REVIEWER_FORGEJO_TOKEN");
        var proxyToken = Required(values, "proxy-token", "TS_REVIEWER_PROXY_TOKEN");
        var maxConcurrent = ParseInt(values, "max-concurrent");
        if (maxConcurrent is < 1 or > 100) throw new InvalidOperationException("max-concurrent must be between 1 and 100");
        var maxDiffBytes = ParseLong(values, "max-diff-bytes");
        if (maxDiffBytes <= 0) throw new InvalidOperationException("max-diff-bytes must be positive");
        if (!TimeSpan.TryParse(values["timeout"], out var reviewTimeout) || reviewTimeout <= TimeSpan.Zero)
            throw new InvalidOperationException("timeout must be positive");
        return new ReviewerOptions(Trim(values["listen"]!), Trim(values["forgejo-url"]!), forgejoToken,
            Trim(values["proxy-url"]!), proxyToken, values["model"]!, values["organization"]!, values["bot-login"]!,
            maxConcurrent, reviewTimeout, maxDiffBytes, values["webhook-secret"]!);
    }
    private static string Required(IReadOnlyDictionary<string, string?> values, string key, string environmentName) =>
        string.IsNullOrWhiteSpace(values[key]) ? throw new InvalidOperationException($"{environmentName} is required") : values[key]!;
    private static int ParseInt(IReadOnlyDictionary<string, string?> values, string key) =>
        int.TryParse(values[key], out var result) ? result : throw new InvalidOperationException($"{key} must be an integer");
    private static long ParseLong(IReadOnlyDictionary<string, string?> values, string key) =>
        long.TryParse(values[key], out var result) ? result : throw new InvalidOperationException($"{key} must be an integer");
    private static string Trim(string value) => value.TrimEnd('/');
}
