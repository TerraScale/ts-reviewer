using System.Net;
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
    internal const int ModelOutputLimit = 128000;
    internal const string FallbackModel = "glm-5.3-flash";
    internal const int MaxAttempts = 3;
    internal static readonly TimeSpan AttemptTimeout = TimeSpan.FromMinutes(20);
    internal static readonly TimeSpan InactivityTimeout = TimeSpan.FromMinutes(20);
    // Leave SDK headroom beyond all attempts and worst-case retry waits.
    internal static readonly TimeSpan StepTimeout = AttemptTimeout * MaxAttempts + TimeSpan.FromMinutes(MaxAttempts + 1);
    private readonly ReviewerOptions _options;
    private readonly ILogger _logger;
    private readonly string _review;
    private readonly HttpClient _http;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeProvider _time;
    private readonly List<string> _models = [];
    private readonly SemaphoreSlim _serial = new(1);
    private CancellationTokenSource? _lifetime;
    private CancellationTokenSource? _inactivity;
    private WebApplication? _app;
    private int _step;

    public ReviewProvider(ReviewerOptions options, ILogger logger, string review, TimeProvider? time = null)
        : this(options, logger, review, new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }), Task.Delay, time) { }

    internal ReviewProvider(ReviewerOptions options, ILogger logger, string review, HttpClient http,
        Func<TimeSpan, CancellationToken, Task> delay, TimeProvider? time = null)
    {
        (_options, _logger, _review, _http, _delay) = (options, logger, review, http, delay);
        _time = time ?? TimeProvider.System;
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    internal string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    internal string Url { get; private set; } = "";
    internal CancellationToken Lifetime => _lifetime!.Token;
    internal string? Failure { get; private set; }
    internal string InitialModel { get; private set; } = "";
    internal string ModelsUsed => _models.Count == 0 ? InitialModel : string.Join(", ", _models);

    internal async Task StartAsync(CancellationToken ct)
    {
        InitialModel = PrimaryModel;
        _inactivity = new CancellationTokenSource(InactivityTimeout, _time);
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, _inactivity.Token);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ApplicationName = typeof(ReviewProvider).Assembly.FullName });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.Listen(IPAddress.Loopback, 0);
            // OCR prompts must not acquire an extra size cap at this authenticated local hop.
            server.Limits.MaxRequestBodySize = null;
        });
        _app = builder.Build();
        _app.MapPost("/v1/messages", HandleAsync);
        await _app.StartAsync(ct);
        Url = _app.Urls.Single();
    }

    private async Task HandleAsync(HttpContext context)
    {
        var request = context.Request;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(request.Headers["x-api-key"].ToString()), Encoding.UTF8.GetBytes(Token))
            && !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(request.Headers.Authorization.ToString()), Encoding.UTF8.GetBytes("Bearer " + Token)))
        {
            await Results.Unauthorized().ExecuteAsync(context);
            return;
        }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _lifetime!.Token);
        var ct = lifetime.Token;
        try
        {
            var body = await JsonNode.ParseAsync(request.Body, cancellationToken: ct) as JsonObject
                ?? throw new InvalidOperationException("model request must be a JSON object");
            // OCR 1.12.12 uses Anthropic Messages.New, not NewStreaming; reject incompatible callers.
            if (body["stream"]?.GetValue<bool>() == true)
                throw new InvalidOperationException("OCR provider accepts only non-streaming requests");
            await SendAsync(body, ct, async (json, token) =>
            {
                context.Response.ContentType = "application/json";
                context.Response.ContentLength = Encoding.UTF8.GetByteCount(json);
                await context.Response.WriteAsync(json, token);
                await context.Response.CompleteAsync().WaitAsync(token);
                token.ThrowIfCancellationRequested();
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException)
        {
            // 400 is deliberately non-retryable to OCR's SDK: its five hidden retries must not reopen this budget.
            if (context.Response.HasStarted) context.Abort();
            else
            {
                context.Response.Clear();
                await Results.Json(new { type = "error", error = new { type = "invalid_request_error", message = ex is JsonException ? "invalid model request JSON" : ex.Message } }, statusCode: 400).ExecuteAsync(context);
            }
        }
    }

    internal async Task<string> SendAsync(JsonObject body, CancellationToken ct, Func<string, CancellationToken, Task>? deliver = null)
    {
        await _serial.WaitAsync(ct);
        // OCR may be idle between calls, but active calls retain their complete retry budget.
        _inactivity?.CancelAfter(Timeout.InfiniteTimeSpan);
        try
        {
            if (Failure is not null) throw new InvalidOperationException(Failure);
            NormalizeRequest(body);
            var step = ++_step;
            string reason = "no response";
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                var model = attempt < MaxAttempts ? PrimaryModel : FallbackModel;
                body["model"] = model;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(AttemptTimeout);
                string? completed = null;
                try
                {
                    var endpoint = _options.GlmUrl.TrimEnd('/');
                    endpoint += endpoint.EndsWith("/v1", StringComparison.Ordinal) ? "/messages" : "/v1/messages";
                    using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                    request.Headers.Authorization = new("Bearer", _options.GlmApiKey);
                    // OCR uses Messages; Z.AI requires the protocol version header for compatibility.
                    request.Headers.Add("anthropic-version", "2023-06-01");
                    request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                    if (!_models.Contains(model)) _models.Add(model);
                    using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                    var json = await response.Content.ReadAsStringAsync(timeout.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        try
                        {
                            ValidateResponse(json, model, body["tools"] as JsonArray);
                            _logger.LogInformation("review {Review}; step {Step}; model {Model}; attempts {Attempts}; retries {Retries}; succeeded",
                                _review, step, model, attempt, attempt - 1);
                            completed = json;
                        }
                        catch (InvalidOperationException ex) { reason = ex.Message; }
                    }
                    else
                    {
                        reason = $"HTTP {(int)response.StatusCode}: {ProviderError(json)}";
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { reason = $"provider timed out after {AttemptTimeout}"; }
                catch (JsonException) { reason = "provider returned malformed JSON"; }
                catch (Exception ex) when (Failure is null && ex is (HttpRequestException or IOException or InvalidOperationException)) { reason = "provider transport failed"; }
                if (completed is not null)
                {
                    // Keep delivery under the call lock; a lost response must not reopen the spent budget.
                    try { if (deliver is not null) await deliver(completed, ct); }
                    catch (Exception) when (_lifetime is { IsCancellationRequested: false })
                    {
                        Failure = "OCR response delivery failed; review stopped to preserve its retry budget";
                        _logger.LogError("review {Review}; step {Step}; {Reason}", _review, step, Failure);
                        throw new InvalidOperationException(Failure);
                    }
                    return completed;
                }
                _logger.LogWarning("review {Review}; step {Step}; model {Model}; attempt {Attempt}/{MaxAttempts} failed: {Reason}",
                    _review, step, model, attempt, MaxAttempts, reason);
                if (attempt < MaxAttempts) await _delay(RetryDelay(attempt, Random.Shared.NextDouble()), ct);
            }
            _logger.LogError("review {Review}; step {Step}; attempts {Attempts}; retries {Retries}; exhausted: {Reason}",
                _review, step, MaxAttempts, MaxAttempts - 1, reason);
            Failure = $"GLM review step {step} failed after {MaxAttempts} attempts: {reason}";
            throw new InvalidOperationException(Failure);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && _lifetime is { IsCancellationRequested: false })
        {
            // Latch before releasing the call lock so OCR cannot reopen a partly spent budget.
            Failure = "OCR disconnected during a model call; review stopped to preserve its retry budget";
            _logger.LogError("review {Review}; step {Step}; {Reason}", _review, _step, Failure);
            throw new InvalidOperationException(Failure);
        }
        finally
        {
            _inactivity?.CancelAfter(InactivityTimeout);
            _serial.Release();
        }
    }

    internal static TimeSpan RetryDelay(int failure, double jitter)
    {
        var ceiling = Math.Min(60, Math.Pow(2, Math.Clamp(failure - 1, 0, 6)));
        return TimeSpan.FromSeconds(ceiling * (0.5 + Math.Clamp(jitter, 0, 1) / 2));
    }

    private static void NormalizeRequest(JsonObject body)
    {
        body["stream"] = false;
        // Let GLM select its required thinking mode while preserving complete tool/thinking history.
        body.Remove("thinking");
        body.Remove("temperature");
        body.Remove("top_p");
        body.Remove("top_k");
        // OCR's grouping call uses its prompt limit as output; GLM accepts at most 128k output.
        if (body["max_tokens"]?.Deserialize<long>() > ModelOutputLimit) body["max_tokens"] = ModelOutputLimit;
        if (body["tools"] is JsonArray { Count: > 0 } tools)
        {
            if (tools.Any(tool => tool?["name"]?.GetValue<string>() is not ("task_done" or "code_comment" or "code_search" or "file_read" or "file_read_diff" or "file_find")
                || tool?["type"]?.GetValue<string>() is not (null or "custom")))
                throw new InvalidOperationException("OCR declared a tool outside the trusted read-only review tool set");
            body["tool_choice"] = new JsonObject { ["type"] = "auto" };
        }
        else body.Remove("tool_choice");
    }

    private static void ValidateResponse(string json, string model, JsonArray? tools)
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
        var hasTool = false;
        var toolIds = new HashSet<string>(StringComparer.Ordinal);
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
                    if (!toolIds.Add(id.GetString()!))
                        throw new InvalidOperationException("provider returned duplicate tool-call IDs");
                    var schema = tools?.FirstOrDefault(tool => tool?["name"]?.GetValue<string>() == name.GetString())?["input_schema"] as JsonObject
                        ?? throw new InvalidOperationException("provider returned a tool not declared in the request");
                    ValidateToolInput(input, schema);
                    usable = true;
                    hasTool = true;
                    break;
                case "thinking":
                    if (!block.TryGetProperty("thinking", out var thinking) || thinking.ValueKind != JsonValueKind.String
                        || (block.TryGetProperty("signature", out var signature) && signature.ValueKind != JsonValueKind.String))
                        throw new InvalidOperationException("provider returned a malformed thinking block");
                    break;
                case "redacted_thinking":
                    if (!block.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(data.GetString()))
                        throw new InvalidOperationException("provider returned a malformed redacted thinking block");
                    break;
                default: throw new InvalidOperationException("provider returned an unsupported content block");
            }
        }
        if (!usable) throw new InvalidOperationException("provider returned no text or tool call");
        if (hasTool != (stop.GetString() == "tool_use"))
            throw new InvalidOperationException("provider returned content inconsistent with stop_reason");
    }

    private static void ValidateToolInput(JsonElement input, JsonObject schema)
    {
        // shortcut: covers pinned OCR 1.12.12 schema keywords; extend when its tool schemas change.
        var validType = schema["type"]?.GetValue<string>() switch
        {
            null => true,
            "object" => input.ValueKind == JsonValueKind.Object,
            "array" => input.ValueKind == JsonValueKind.Array,
            "string" => input.ValueKind == JsonValueKind.String,
            "boolean" => input.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "number" => input.ValueKind == JsonValueKind.Number,
            "integer" => input.ValueKind == JsonValueKind.Number && input.TryGetDecimal(out var number) && decimal.Truncate(number) == number,
            "null" => input.ValueKind == JsonValueKind.Null,
            _ => false,
        };
        if (!validType || (schema["enum"] is JsonArray choices && !choices.Any(value => JsonElement.DeepEquals(input, JsonSerializer.SerializeToElement(value)))))
            throw new InvalidOperationException("provider returned schema-invalid tool arguments");
        if (input.ValueKind == JsonValueKind.Object)
        {
            if (schema["required"] is JsonArray required)
                foreach (var field in required)
                    if (!input.TryGetProperty(field!.GetValue<string>(), out _))
                        throw new InvalidOperationException("provider returned schema-invalid tool arguments: missing required field");
            if (schema["properties"] is JsonObject properties)
                foreach (var (field, property) in properties)
                    if (input.TryGetProperty(field, out var value) && property is JsonObject propertySchema)
                        ValidateToolInput(value, propertySchema);
        }
        if (input.ValueKind == JsonValueKind.Array && schema["items"] is JsonObject items)
            foreach (var item in input.EnumerateArray()) ValidateToolInput(item, items);
    }

    private static string ProviderError(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
            {
                // Upstream messages and transport exceptions can echo keys, URLs or prompt content.
                return type.GetString() switch
                {
                    "invalid_request_error" or "authentication_error" or "permission_error" or "not_found_error"
                        or "request_too_large" or "rate_limit_error" or "api_error" or "overloaded_error" => type.GetString()!,
                    _ => "provider request failed",
                };
            }
        }
        catch (JsonException) { }
        return "provider request failed";
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime?.Cancel();
        if (_app is not null) await _app.DisposeAsync();
        _lifetime?.Dispose();
        _inactivity?.Dispose();
        _http.Dispose();
        _serial.Dispose();
    }
}
