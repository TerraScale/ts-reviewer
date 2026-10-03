namespace TerraScale.Reviewer.Features.Configuration;

public sealed record ReviewerOptions(
    string ListenUrl, string ForgejoUrl, string ForgejoToken, string ProxyUrl, string ProxyToken,
    string Model, string Organization, string BotLogin, int MaxConcurrent, TimeSpan ReviewTimeout,
    string WebhookSecret, string ContextApiToken, string ContextDatabasePath, int ContextMaxFiles,
    int ContextMaxAttempts = 5, TimeSpan? ContextRetryBaseDelay = null, string OperatorToken = "",
    string EscalationModel = "gpt-6-astra", int OcrConcurrency = 16, int OcrMaxAttempts = 3,
    TimeSpan? OcrRetryBaseDelay = null, TimeSpan? OcrLlmRequestTimeout = null, TimeSpan? OcrItemReviewTimeout = null,
    string FallbackModel = "", int PrimaryStreams = 64)
{
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
            ["listen"] = Environment.GetEnvironmentVariable("TS_REVIEWER_LISTEN") ?? "http://+:8080",
            ["forgejo-url"] = Environment.GetEnvironmentVariable("TS_REVIEWER_FORGEJO_URL") ?? "https://git.terrascale.tech",
            ["forgejo-token"] = Environment.GetEnvironmentVariable("TS_REVIEWER_FORGEJO_TOKEN"),
            ["proxy-url"] = Environment.GetEnvironmentVariable("TS_REVIEWER_PROXY_URL") ?? "http://10.11.99.1:8318",
            ["proxy-token"] = Environment.GetEnvironmentVariable("TS_REVIEWER_PROXY_TOKEN"),
            ["model"] = Environment.GetEnvironmentVariable("TS_REVIEWER_MODEL") ?? "gpt-6.1-sol(medium)",
            ["escalation-model"] = Environment.GetEnvironmentVariable("TS_REVIEWER_ESCALATION_MODEL") ?? "gpt-6-astra",
            ["fallback-model"] = Environment.GetEnvironmentVariable("TS_REVIEWER_FALLBACK_MODEL") ?? string.Empty,
            ["organization"] = Environment.GetEnvironmentVariable("TS_REVIEWER_ORGANIZATION") ?? "TerraScale",
            ["bot-login"] = Environment.GetEnvironmentVariable("TS_REVIEWER_BOT_LOGIN") ?? "terrascale-bot",
            ["primary-streams"] = Environment.GetEnvironmentVariable("TS_REVIEWER_PRIMARY_STREAMS") ?? "64",
            ["max-concurrent"] = Environment.GetEnvironmentVariable("TS_REVIEWER_MAX_CONCURRENT") ?? "32",
            ["ocr-concurrency"] = Environment.GetEnvironmentVariable("TS_REVIEWER_OCR_CONCURRENCY") ?? "16",
            ["ocr-max-attempts"] = Environment.GetEnvironmentVariable("TS_REVIEWER_OCR_MAX_ATTEMPTS") ?? "3",
            ["ocr-retry-base-delay"] = Environment.GetEnvironmentVariable("TS_REVIEWER_OCR_RETRY_BASE_DELAY") ?? "00:00:10",
            ["ocr-llm-timeout"] = Environment.GetEnvironmentVariable("TS_REVIEWER_OCR_LLM_TIMEOUT") ?? "00:20:00",
            ["ocr-item-timeout"] = Environment.GetEnvironmentVariable("TS_REVIEWER_OCR_ITEM_TIMEOUT") ?? "01:00:00",
            ["timeout"] = Environment.GetEnvironmentVariable("TS_REVIEWER_TIMEOUT") ?? "04:00:00",
            ["webhook-secret"] = Environment.GetEnvironmentVariable("TS_REVIEWER_WEBHOOK_SECRET") ?? string.Empty,
            ["context-api-token"] = Environment.GetEnvironmentVariable("TS_REVIEWER_CONTEXT_API_TOKEN") ?? string.Empty,
            ["context-database"] = Environment.GetEnvironmentVariable("TS_REVIEWER_CONTEXT_DATABASE") ?? "data/reviewer-context.db",
            ["context-max-files"] = Environment.GetEnvironmentVariable("TS_REVIEWER_CONTEXT_MAX_FILES") ?? "20000",
            ["context-max-attempts"] = Environment.GetEnvironmentVariable("TS_REVIEWER_CONTEXT_MAX_ATTEMPTS") ?? "5",
            ["context-retry-base-delay"] = Environment.GetEnvironmentVariable("TS_REVIEWER_CONTEXT_RETRY_BASE_DELAY") ?? "00:00:01",
            ["operator-token"] = Environment.GetEnvironmentVariable("TS_REVIEWER_OPERATOR_TOKEN") ?? string.Empty
        };
        foreach (var arg in args)
        {
            if (!arg.StartsWith("--", StringComparison.Ordinal) || !arg.Contains('=')) continue;
            var pair = arg[2..].Split('=', 2);
            values[pair[0]] = pair[1];
        }
        var forgejoToken = Required(values, "forgejo-token", "TS_REVIEWER_FORGEJO_TOKEN");
        var proxyToken = Required(values, "proxy-token", "TS_REVIEWER_PROXY_TOKEN");
        var webhookSecret = Required(values, "webhook-secret", "TS_REVIEWER_WEBHOOK_SECRET");
        var contextApiToken = Required(values, "context-api-token", "TS_REVIEWER_CONTEXT_API_TOKEN");
        var operatorToken = Required(values, "operator-token", "TS_REVIEWER_OPERATOR_TOKEN");
        var maxConcurrent = ParseInt(values, "max-concurrent");
        if (maxConcurrent is < 1 or > 100) throw new InvalidOperationException("max-concurrent must be between 1 and 100");
        var primaryStreams = ParseInt(values, "primary-streams");
        if (primaryStreams is < 1 or > 1000) throw new InvalidOperationException("primary-streams must be between 1 and 1000");
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
            Trim(values["proxy-url"]!), proxyToken, values["model"]!, values["organization"]!, values["bot-login"]!,
            maxConcurrent, reviewTimeout, webhookSecret, contextApiToken, values["context-database"]!, contextMaxFiles,
            contextMaxAttempts, contextRetryBaseDelay, operatorToken, values["escalation-model"]!, ocrConcurrency,
            ocrMaxAttempts, ocrRetryBaseDelay, ocrLlmTimeout, ocrItemTimeout, (values["fallback-model"] ?? string.Empty).Trim(), primaryStreams);
    }
    private static string Required(IReadOnlyDictionary<string, string?> values, string key, string environmentName) =>
        string.IsNullOrWhiteSpace(values[key]) ? throw new InvalidOperationException($"{environmentName} is required") : values[key]!;
    private static int ParseInt(IReadOnlyDictionary<string, string?> values, string key) =>
        int.TryParse(values[key], out var result) ? result : throw new InvalidOperationException($"{key} must be an integer");
    private static string Trim(string value) => value.TrimEnd('/');
}
