using System.Text.Json;

namespace TerraScale.Reviewer.Features.Configuration;

public sealed record ReviewerOptions(
    string ListenUrl, string ForgejoUrl, string ForgejoToken, string ProxyUrl, string ProxyToken,
    string Organization, string BotLogin, TimeSpan ReviewTimeout,
    string WebhookSecret, string ContextApiToken, string ContextDatabasePath, int ContextMaxFiles,
    int ContextMaxAttempts = 5, TimeSpan? ContextRetryBaseDelay = null, string OperatorToken = "")
{
    public TimeSpan ContextRetryDelay => ContextRetryBaseDelay ?? TimeSpan.FromSeconds(1);
    public static ReviewerOptions Load(string[] args)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["listen"] = "http://+:8080",
            ["forgejo-url"] = "https://git.terrascale.tech",
            ["forgejo-token"] = null,
            ["proxy-url"] = "http://10.11.99.1:8318",
            ["proxy-token"] = null,
            ["organization"] = "TerraScale",
            ["bot-login"] = "terrascale-bot",
            ["timeout"] = "04:00:00",
            ["webhook-secret"] = string.Empty,
            ["context-api-token"] = string.Empty,
            ["context-database"] = "data/reviewer-context.db",
            ["context-max-files"] = "20000",
            ["context-max-attempts"] = "5",
            ["context-retry-base-delay"] = "00:00:01",
            ["operator-token"] = string.Empty
        };
        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var arg in args)
        {
            if (!arg.StartsWith("--", StringComparison.Ordinal) || !arg.Contains('=')) continue;
            var pair = arg[2..].Split('=', 2);
            overrides[pair[0]] = pair[1];
        }
        var configPath = overrides.TryGetValue("config", out var path)
            ? path : Environment.GetEnvironmentVariable("TS_REVIEWER_CONFIG");
        if (configPath is not null)
        {
            if (string.IsNullOrWhiteSpace(configPath)) throw new InvalidOperationException("config path must not be blank");
            try
            {
                using var config = JsonDocument.Parse(File.ReadAllText(configPath));
                if (config.RootElement.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException("config must be a JSON object with kebab-case option keys");
                foreach (var property in config.RootElement.EnumerateObject())
                {
                    if (!values.ContainsKey(property.Name)) throw new InvalidOperationException($"unknown config option: {property.Name}");
                    values[property.Name] = property.Value.ValueKind switch
                    {
                        JsonValueKind.String => property.Value.GetString(),
                        JsonValueKind.Number => property.Value.GetRawText(),
                        _ => throw new InvalidOperationException($"config option {property.Name} must be a string or number")
                    };
                }
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"could not read JSON config file: {configPath}", exception);
            }
        }
        foreach (var key in values.Keys.ToArray())
        {
            var environmentValue = Environment.GetEnvironmentVariable("TS_REVIEWER_" + key.Replace('-', '_').ToUpperInvariant());
            if (environmentValue is not null) values[key] = environmentValue;
        }
        foreach (var (key, value) in overrides)
        {
            if (key == "config") continue;
            if (!values.ContainsKey(key)) throw new InvalidOperationException($"unknown option: {key}");
            values[key] = value;
        }
        var forgejoToken = Required(values, "forgejo-token", "TS_REVIEWER_FORGEJO_TOKEN");
        var proxyToken = Required(values, "proxy-token", "TS_REVIEWER_PROXY_TOKEN");
        var webhookSecret = Required(values, "webhook-secret", "TS_REVIEWER_WEBHOOK_SECRET");
        var contextApiToken = Required(values, "context-api-token", "TS_REVIEWER_CONTEXT_API_TOKEN");
        var operatorToken = Required(values, "operator-token", "TS_REVIEWER_OPERATOR_TOKEN");
        if (!TimeSpan.TryParse(values["timeout"], out var reviewTimeout) || reviewTimeout <= TimeSpan.Zero)
            throw new InvalidOperationException("timeout must be positive");
        var contextMaxFiles = ParseInt(values, "context-max-files");
        if (contextMaxFiles is < 1 or > 100_000) throw new InvalidOperationException("context-max-files must be between 1 and 100000");
        var contextMaxAttempts = ParseInt(values, "context-max-attempts");
        if (contextMaxAttempts is < 1 or > 100) throw new InvalidOperationException("context-max-attempts must be between 1 and 100");
        if (!TimeSpan.TryParse(values["context-retry-base-delay"], out var contextRetryBaseDelay)
            || contextRetryBaseDelay <= TimeSpan.Zero || contextRetryBaseDelay > TimeSpan.FromMinutes(1))
            throw new InvalidOperationException("context-retry-base-delay must be positive and at most one minute");
        return new ReviewerOptions(Trim(values["listen"]!), Trim(values["forgejo-url"]!), forgejoToken,
            Trim(values["proxy-url"]!), proxyToken, values["organization"]!, values["bot-login"]!,
            reviewTimeout, webhookSecret, contextApiToken, values["context-database"]!, contextMaxFiles,
            contextMaxAttempts, contextRetryBaseDelay, operatorToken);
    }
    private static string Required(IReadOnlyDictionary<string, string?> values, string key, string environmentName) =>
        string.IsNullOrWhiteSpace(values[key]) ? throw new InvalidOperationException($"{environmentName} is required") : values[key]!;
    private static int ParseInt(IReadOnlyDictionary<string, string?> values, string key) =>
        int.TryParse(values[key], out var result) ? result : throw new InvalidOperationException($"{key} must be an integer");
    private static string Trim(string value) => value.TrimEnd('/');
}
