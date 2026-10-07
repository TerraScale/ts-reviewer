using System.Text.Json;

namespace TerraScale.Reviewer.Features.Configuration;

public sealed record ReviewerOptions(
    string ListenUrl, string ForgejoUrl, string ForgejoToken, string ProxyUrl, string ProxyToken,
    string Model, string Organization, string BotLogin, int MaxConcurrent, TimeSpan ReviewTimeout,
    string WebhookSecret, string ContextApiToken, string ContextDatabasePath, int ContextMaxFiles,
    int ContextMaxAttempts = 5, TimeSpan? ContextRetryBaseDelay = null, string OperatorToken = "",
    string EscalationModel = "gpt-6.1-sol", int OcrConcurrency = 16, int OcrMaxAttempts = 3,
    TimeSpan? OcrRetryBaseDelay = null, TimeSpan? OcrLlmRequestTimeout = null, TimeSpan? OcrItemReviewTimeout = null,
    string FallbackModel = "glm-5.3",
    int SolWeight = 60, int GlmWeight = 40, int SolSlots = 2, int GlmSlots = 1,
    TimeSpan? ModelSlotWaitDuration = null, TimeSpan? ModelBackoffBaseDelay = null,
    TimeSpan? ModelBackoffMaximumDelay = null)
{
    public TimeSpan ModelSlotWait => ModelSlotWaitDuration ?? TimeSpan.FromMinutes(2);
    public TimeSpan ModelBackoffDelay => ModelBackoffBaseDelay ?? TimeSpan.FromSeconds(30);
    public TimeSpan ModelBackoffMaxDelay => ModelBackoffMaximumDelay ?? TimeSpan.FromMinutes(10);
    public TimeSpan ContextRetryDelay => ContextRetryBaseDelay ?? TimeSpan.FromSeconds(1);
    public TimeSpan OcrRetryDelay => OcrRetryBaseDelay ?? TimeSpan.FromSeconds(10);
    /// <summary>Per-request LLM timeout handed to ocr as <c>OCR_LLM_TIMEOUT</c>.</summary>
    public TimeSpan OcrLlmTimeout => OcrLlmRequestTimeout ?? TimeSpan.FromMinutes(20);
    /// <summary>Per-item (file group) deadline handed to ocr as <c>--timeout</c>.</summary>
    public TimeSpan OcrItemTimeout => OcrItemReviewTimeout ?? TimeSpan.FromHours(1);

    public static ReviewerOptions Load(string[] args)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["listen"] = "http://+:8080",
            ["forgejo-url"] = "https://git.terrascale.tech",
            ["forgejo-token"] = null,
            ["proxy-url"] = "http://10.11.99.1:8318",
            ["proxy-token"] = null,
            ["model"] = "gpt-6.1-sol",
            ["escalation-model"] = "gpt-6.1-sol",
            ["fallback-model"] = "glm-5.3",
            ["organization"] = "TerraScale",
            ["bot-login"] = "terrascale-bot",
            ["max-concurrent"] = "2",
            ["sol-weight"] = "60",
            ["glm-weight"] = "40",
            ["sol-slots"] = "2",
            ["glm-slots"] = "1",
            ["model-slot-wait"] = "00:02:00",
            ["model-backoff-base-delay"] = "00:00:30",
            ["model-backoff-max-delay"] = "00:10:00",
            ["ocr-concurrency"] = "16",
            ["ocr-max-attempts"] = "3",
            ["ocr-retry-base-delay"] = "00:00:10",
            ["ocr-llm-timeout"] = "00:20:00",
            ["ocr-item-timeout"] = "01:00:00",
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
        foreach (var (key, value) in overrides) values[key] = value;
        var forgejoToken = Required(values, "forgejo-token", "TS_REVIEWER_FORGEJO_TOKEN");
        var proxyToken = Required(values, "proxy-token", "TS_REVIEWER_PROXY_TOKEN");
        var webhookSecret = Required(values, "webhook-secret", "TS_REVIEWER_WEBHOOK_SECRET");
        var contextApiToken = Required(values, "context-api-token", "TS_REVIEWER_CONTEXT_API_TOKEN");
        var operatorToken = Required(values, "operator-token", "TS_REVIEWER_OPERATOR_TOKEN");
        var model = ValidateModel(values, "model", "gpt-6.1-sol");
        var escalationModel = ValidateModel(values, "escalation-model", "gpt-6.1-sol");
        var fallbackModel = ValidateModel(values, "fallback-model", "glm-5.3");
        var maxConcurrent = ParseInt(values, "max-concurrent");
        if (maxConcurrent is < 1 or > 2) throw new InvalidOperationException("max-concurrent must be between 1 and 2");
        var solSlots = ParseInt(values, "sol-slots");
        if (solSlots is < 1 or > 2) throw new InvalidOperationException("sol-slots must be between 1 and 2");
        var glmSlots = ParseInt(values, "glm-slots");
        if (glmSlots != 1) throw new InvalidOperationException("glm-slots must be 1");
        var solWeight = ParseInt(values, "sol-weight");
        var glmWeight = ParseInt(values, "glm-weight");
        if (solWeight <= 0 || glmWeight <= 0 || (long)solWeight + glmWeight != 100)
            throw new InvalidOperationException("sol-weight and glm-weight must be positive integers totaling 100");
        var modelSlotWait = ParsePositiveDuration(values, "model-slot-wait");
        var modelBackoffDelay = ParsePositiveDuration(values, "model-backoff-base-delay");
        var modelBackoffMaxDelay = ParsePositiveDuration(values, "model-backoff-max-delay");
        if (modelBackoffMaxDelay < modelBackoffDelay)
            throw new InvalidOperationException("model-backoff-max-delay must be at least model-backoff-base-delay");
        var ocrConcurrency = ParseInt(values, "ocr-concurrency");
        if (ocrConcurrency is < 1 or > 64) throw new InvalidOperationException("ocr-concurrency must be between 1 and 64");
        var ocrMaxAttempts = ParseInt(values, "ocr-max-attempts");
        if (ocrMaxAttempts is < 1 or > 20) throw new InvalidOperationException("ocr-max-attempts must be between 1 and 20");
        if (!TimeSpan.TryParse(values["ocr-retry-base-delay"], out var ocrRetryBaseDelay)
            || ocrRetryBaseDelay <= TimeSpan.Zero || ocrRetryBaseDelay > TimeSpan.FromMinutes(5))
            throw new InvalidOperationException("ocr-retry-base-delay must be positive and at most five minutes");
        if (!TimeSpan.TryParse(values["ocr-llm-timeout"], out var ocrLlmTimeout)
            || ocrLlmTimeout < TimeSpan.FromMinutes(1) || ocrLlmTimeout > TimeSpan.FromHours(2))
            throw new InvalidOperationException("ocr-llm-timeout must be between one minute and two hours");
        if (!TimeSpan.TryParse(values["ocr-item-timeout"], out var ocrItemTimeout)
            || ocrItemTimeout < TimeSpan.FromMinutes(1) || ocrItemTimeout > TimeSpan.FromHours(24))
            throw new InvalidOperationException("ocr-item-timeout must be between one minute and 24 hours");
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
            Trim(values["proxy-url"]!), proxyToken, model, values["organization"]!, values["bot-login"]!,
            maxConcurrent, reviewTimeout, webhookSecret, contextApiToken, values["context-database"]!, contextMaxFiles,
            contextMaxAttempts, contextRetryBaseDelay, operatorToken, escalationModel, ocrConcurrency,
            ocrMaxAttempts, ocrRetryBaseDelay, ocrLlmTimeout, ocrItemTimeout, fallbackModel,
            solWeight, glmWeight, solSlots, glmSlots, modelSlotWait, modelBackoffDelay, modelBackoffMaxDelay);
    }
    private static string Required(IReadOnlyDictionary<string, string?> values, string key, string environmentName) =>
        string.IsNullOrWhiteSpace(values[key]) ? throw new InvalidOperationException($"{environmentName} is required") : values[key]!;
    private static int ParseInt(IReadOnlyDictionary<string, string?> values, string key) =>
        int.TryParse(values[key], out var result) ? result : throw new InvalidOperationException($"{key} must be an integer");
    private static TimeSpan ParsePositiveDuration(IReadOnlyDictionary<string, string?> values, string key) =>
        TimeSpan.TryParse(values[key], out var duration) && duration > TimeSpan.Zero
            ? duration : throw new InvalidOperationException($"{key} must be a positive duration");
    private static string ValidateModel(IReadOnlyDictionary<string, string?> values, string key, string canonicalModel)
    {
        var model = (values[key] ?? string.Empty).Trim();
        if (model.Contains("astra", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{key} cannot contain astra");
        if (!string.Equals(model, canonicalModel, StringComparison.Ordinal))
            throw new InvalidOperationException($"{key} must be {canonicalModel}");
        return model;
    }
    private static string Trim(string value) => value.TrimEnd('/');
}
