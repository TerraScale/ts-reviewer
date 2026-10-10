using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TerraScale.Reviewer.Features.Configuration;

namespace TerraScale.Reviewer.Features.Review;

/// <summary>Owns the retry budget for each OCR model call, before OCR's SDK can retry it.</summary>
internal sealed class ReviewProvider : IAsyncDisposable
{
    internal const string PrimaryModel = "glm-5.3";
    internal const string FallbackModel = "glm-5.3-flash";
    internal const int MaxAttempts = 30;
    internal static readonly TimeSpan AttemptTimeout = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan StepTimeout = TimeSpan.FromHours(3);
    private readonly ReviewerOptions _options;
    private readonly ILogger _logger;
    private readonly string _review;
    private readonly HttpClient _http;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SemaphoreSlim _serial = new(1);
    private readonly HashSet<string> _models = [];
    private WebApplication? _app;
    private int _step;

    public ReviewProvider(ReviewerOptions options, ILogger logger, string review)
        : this(options, logger, review, new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }), Task.Delay) { }

    internal ReviewProvider(ReviewerOptions options, ILogger logger, string review, HttpClient http,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        (_options, _logger, _review, _http, _delay) = (options, logger, review, http, delay);
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    internal string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    internal string Url { get; private set; } = "";
    internal string? Failure { get; private set; }
    internal string ModelsUsed => _models.Count == 0 ? PrimaryModel : string.Join(" → ", _models);

    internal async Task StartAsync(CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ApplicationName = typeof(ReviewProvider).Assembly.FullName });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        _app = builder.Build();
        _app.MapPost("/v1/messages", HandleAsync);
        await _app.StartAsync(ct);
        Url = _app.Urls.Single();
    }

    private async Task<IResult> HandleAsync(HttpRequest request, CancellationToken ct)
    {
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(request.Headers["x-api-key"].ToString()), Encoding.UTF8.GetBytes(Token))
            && !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(request.Headers.Authorization.ToString()), Encoding.UTF8.GetBytes("Bearer " + Token)))
            return Results.Unauthorized();
        try
        {
            var body = await JsonNode.ParseAsync(request.Body, cancellationToken: ct) as JsonObject
                ?? throw new InvalidOperationException("model request must be a JSON object");
            return Results.Text(await SendAsync(body, ct), "application/json");
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException)
        {
            // 400 is deliberately non-retryable to OCR's SDK: its five hidden retries must not reopen this budget.
            return Results.Json(new { type = "error", error = new { type = "invalid_request_error", message = ex.Message } }, statusCode: 400);
        }
    }

    internal async Task<string> SendAsync(JsonObject body, CancellationToken ct)
    {
        await _serial.WaitAsync(ct);
        try
        {
            if (Failure is not null) throw new InvalidOperationException(Failure);
            var step = ++_step;
            string reason = "no response";
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                // Alternate in blocks of three consecutive failures; every new step starts with GLM 5.3.
                var model = ((attempt - 1) / 3) % 2 == 0 ? PrimaryModel : FallbackModel;
                body["model"] = model;
                body["stream"] = false;
                _models.Add(model);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(AttemptTimeout);
                try
                {
                    var endpoint = _options.ProxyUrl.TrimEnd('/');
                    endpoint += endpoint.EndsWith("/v1", StringComparison.Ordinal) ? "/messages" : "/v1/messages";
                    using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ProxyToken);
                    request.Headers.Add("anthropic-version", "2023-06-01");
                    request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                    using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                    var json = await response.Content.ReadAsStringAsync(timeout.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        ValidateResponse(json, model);
                        _logger.LogInformation("review {Review}; step {Step}; model {Model}; attempts {Attempts}; retries {Retries}; succeeded",
                            _review, step, model, attempt, attempt - 1);
                        return json;
                    }
                    reason = $"HTTP {(int)response.StatusCode}: {ProviderError(json)}";
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { reason = $"provider timed out after {AttemptTimeout}"; }
                catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException) { reason = ex.Message; }
                _logger.LogWarning("review {Review}; step {Step}; model {Model}; attempt {Attempt}/{MaxAttempts} failed: {Reason}",
                    _review, step, model, attempt, MaxAttempts, reason);
                if (attempt < MaxAttempts) await _delay(RetryDelay(attempt, Random.Shared.NextDouble()), ct);
            }
            _logger.LogError("review {Review}; step {Step}; attempts {Attempts}; retries {Retries}; exhausted: {Reason}",
                _review, step, MaxAttempts, MaxAttempts - 1, reason);
            Failure = $"GLM review step {step} failed after {MaxAttempts} attempts: {reason}";
            throw new InvalidOperationException(Failure);
        }
        finally { _serial.Release(); }
    }

    internal static TimeSpan RetryDelay(int failure, double jitter)
    {
        var ceiling = Math.Min(60, Math.Pow(2, Math.Clamp(failure - 1, 0, 6)));
        return TimeSpan.FromSeconds(ceiling * (0.5 + Math.Clamp(jitter, 0, 1) / 2));
    }

    private static void ValidateResponse(string json, string model)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("type", out var type) || type.GetString() != "message"
            || !root.TryGetProperty("id", out var messageId) || messageId.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(messageId.GetString())
            || !root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object
            || !usage.TryGetProperty("input_tokens", out var inputTokens) || !inputTokens.TryGetInt64(out var inputCount) || inputCount < 0
            || !usage.TryGetProperty("output_tokens", out var outputTokens) || !outputTokens.TryGetInt64(out var outputCount) || outputCount < 0
            || !root.TryGetProperty("role", out var role) || role.GetString() != "assistant"
            || !root.TryGetProperty("model", out var returned) || !string.Equals(returned.GetString(), model, StringComparison.OrdinalIgnoreCase)
            || !root.TryGetProperty("stop_reason", out var stop) || stop.GetString() is not ("end_turn" or "tool_use" or "stop_sequence")
            || !root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array || content.GetArrayLength() == 0)
            throw new InvalidOperationException("provider returned an empty, malformed or unexpected-model response");
        var usable = false;
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object || !block.TryGetProperty("type", out var kind))
                throw new InvalidOperationException("provider returned a malformed content block");
            switch (kind.GetString())
            {
                case "text":
                    if (!block.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                        throw new InvalidOperationException("provider returned malformed text");
                    usable |= !string.IsNullOrWhiteSpace(text.GetString());
                    break;
                case "tool_use":
                    if (!block.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString())
                        || !block.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString())
                        || !block.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object)
                        throw new InvalidOperationException("provider returned a malformed tool call");
                    usable = true;
                    break;
                case "thinking" or "redacted_thinking": break;
                default: throw new InvalidOperationException("provider returned an unsupported content block");
            }
        }
        if (!usable) throw new InvalidOperationException("provider returned no text or tool call");
    }

    private static string ProviderError(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
            {
                var reason = message.GetString()!;
                return reason[..Math.Min(reason.Length, 300)];
            }
        }
        catch (JsonException) { }
        return "provider request failed";
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
        _http.Dispose();
        _serial.Dispose();
    }
}
