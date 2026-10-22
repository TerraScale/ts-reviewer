using System.Text.Json;

namespace TerraScale.Reviewer.Features.Configuration;

public sealed record ReviewerOptions(
    string ListenUrl, string GitHubUrl, string GitHubAppId, string GlmUrl, string GlmApiKey,
    string Organization, string BotLogin,
    string WebhookSecret, string ContextApiToken, string ContextDatabasePath, int ContextMaxFiles,
    int ContextMaxAttempts = 5, TimeSpan? ContextRetryBaseDelay = null, string OperatorToken = "",
    string GitHubInstallationId = "", string GitHubPrivateKeyPath = "")
{
    public TimeSpan ContextRetryDelay => ContextRetryBaseDelay ?? TimeSpan.FromSeconds(1);
    public static ReviewerOptions Load(string[] args)
    {
        if (Environment.GetEnvironmentVariables().Keys.Cast<string>().Any(key =>
            key.StartsWith("TS_REVIEWER_ANTHROPIC_", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("retired Anthropic configuration is not supported");
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["listen"] = "http://+:8080",
            ["github-app-id"] = null,
            ["github-installation-id"] = null,
            ["github-private-key-path"] = null,
            ["glm-api-key"] = null,
            ["bot-login"] = null,
            ["webhook-secret"] = string.Empty,
            ["context-api-token"] = string.Empty,
            ["context-database"] = "data/reviewer-context.db",
            ["operator-token"] = string.Empty
        };
        string[] secretKeys = ["glm-api-key", "webhook-secret", "context-api-token", "operator-token"];
        foreach (var key in secretKeys) values[key + "-file"] = null;
        var supplied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                    supplied.Add(property.Name);
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
            if (environmentValue is not null)
            {
                supplied.Add(key);
                values[key] = environmentValue;
            }
        }
        foreach (var (key, value) in overrides)
        {
            if (string.Equals(key, "config", StringComparison.OrdinalIgnoreCase)) continue;
            if (!values.ContainsKey(key)) throw new InvalidOperationException($"unknown option: {key}");
            supplied.Add(key);
            values[key] = value;
        }
        // CLI > environment > JSON for each key; plain/file presence conflicts across every source.
        foreach (var key in secretKeys)
        {
            if (!supplied.Contains(key + "-file")) continue;
            if (supplied.Contains(key)) throw new InvalidOperationException($"{key} and {key}-file cannot both be supplied");
            values[key] = ReadSecretFile(values[key + "-file"], key);
        }
        var appId = Required(values, "github-app-id", "TS_REVIEWER_GITHUB_APP_ID");
        var installationId = Required(values, "github-installation-id", "TS_REVIEWER_GITHUB_INSTALLATION_ID");
        var privateKeyPath = Required(values, "github-private-key-path", "TS_REVIEWER_GITHUB_PRIVATE_KEY_PATH");
        var glmKey = Required(values, "glm-api-key", "TS_REVIEWER_GLM_API_KEY");
        var botLogin = Required(values, "bot-login", "TS_REVIEWER_BOT_LOGIN");
        if (!long.TryParse(appId, out var parsedAppId) || parsedAppId <= 0
            || !long.TryParse(installationId, out var parsedInstallationId) || parsedInstallationId <= 0)
            throw new InvalidOperationException("GitHub App and installation IDs must be positive integers");
        var webhookSecret = Required(values, "webhook-secret", "TS_REVIEWER_WEBHOOK_SECRET");
        var contextApiToken = Required(values, "context-api-token", "TS_REVIEWER_CONTEXT_API_TOKEN");
        var operatorToken = Required(values, "operator-token", "TS_REVIEWER_OPERATOR_TOKEN");
        return new ReviewerOptions(Trim(values["listen"]!), "https://api.github.com", appId,
            "https://api.z.ai/api/anthropic", glmKey, "TerraScale", botLogin,
            webhookSecret, contextApiToken, values["context-database"]!, 20_000,
            5, TimeSpan.FromSeconds(1), operatorToken, installationId, privateKeyPath);
    }
    private static string ReadSecretFile(string? path, string key)
    {
        var error = $"{key}-file must name a readable file containing a nonblank secret";
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException(error);
        string value;
        try { value = File.ReadAllText(path).TrimEnd('\r', '\n'); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // File exception messages include mount paths; do not propagate them.
            throw new InvalidOperationException(error);
        }
        return string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException(error) : value;
    }
    private static string Required(IReadOnlyDictionary<string, string?> values, string key, string environmentName) =>
        string.IsNullOrWhiteSpace(values[key]) ? throw new InvalidOperationException($"{environmentName} is required") : values[key]!;
    private static string Trim(string value) => value.TrimEnd('/');
}
