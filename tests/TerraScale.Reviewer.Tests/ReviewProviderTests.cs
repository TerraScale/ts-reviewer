using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TerraScale.Reviewer.Features.Configuration;
using TerraScale.Reviewer.Features.Review;
using TUnit.Assertions;
using TUnit.Core;

public sealed class ReviewProviderTests
{
    private static readonly ReviewerOptions Options = new("http://+:8080", "https://github.test", "github-app",
        "https://anthropic.test", "anthropic-secret", "TerraScale", "bot", "webhook", "context", "unused", 100);

    private static JsonObject Request() => new()
    {
        ["model"] = "ignored",
        ["messages"] = new JsonArray(),
        ["stream"] = false,
        ["tools"] = JsonNode.Parse("""
            [{"name":"code_search","input_schema":{"type":"object","properties":{
              "search_text":{"type":"string"},"file_patterns":{"type":"array","items":{"type":"string"}},
              "case_sensitive":{"type":"boolean"},"use_perl_regexp":{"type":"boolean"}},"required":["search_text"]}},
             {"name":"file_read","input_schema":{"type":"object","properties":{
              "file_path":{"type":"string"},"start_line":{"type":"integer"},"end_line":{"type":"integer"}},"required":["file_path"]}},
             {"name":"task_done","input_schema":{"type":"object","properties":{
              "state":{"type":"string","enum":["DONE","FAILED"]}},"required":["state"]}},
             {"name":"code_comment","input_schema":{"type":"object","properties":{
              "comments":{"type":"array","items":{"type":"object","properties":{
                "content":{"type":"string"},"existing_code":{"type":"string"},"suggestion_code":{"type":"string"},
                "category":{"type":"string","enum":["bug","security","performance","maintainability","test","style","documentation","other"]},
                "severity":{"type":"string","enum":["critical","high","medium","low"]},"path":{"type":"string"}},
                "required":["content","existing_code","category","severity","path"]}}},"required":["comments"]}}]
            """)
    };

    private static HttpResponseMessage Success(string model, object[]? content = null, string stopReason = "end_turn") => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new
        {
            id = "msg_1",
            type = "message",
            role = "assistant",
            model,
            stop_reason = stopReason,
            usage = new { input_tokens = 10, output_tokens = 3 },
            content = content ?? [new { type = "text", text = "Reviewed." }]
        })
    };

    [Test]
    [Arguments("any", "disabled", 200000, 128000)]
    [Arguments("tool", "enabled", 16384, 16384)]
    [Arguments("auto", "adaptive", 4096, 4096)]
    public async Task NormalizesPinnedOcrOptionsWithoutChangingSignedHistory(string choice, string thinking, int requested, int expected)
    {
        var transport = new Transport((_, model, _) => Task.FromResult(Success(model)));
        await using var provider = Create(transport);
        var request = Request();
        request["tool_choice"] = new JsonObject { ["type"] = choice, ["name"] = "file_read" };
        request["thinking"] = new JsonObject { ["type"] = thinking, ["budget_tokens"] = 1024 };
        request["temperature"] = 0.2;
        request["top_p"] = 0.5;
        request["top_k"] = 5;
        request["max_tokens"] = requested;
        request["messages"] = JsonNode.Parse("""
            [{"role":"assistant","content":[{"type":"thinking","thinking":"","signature":"signed-history"},
              {"type":"redacted_thinking","data":"redacted-history"},
              {"type":"tool_use","id":"read_1","name":"file_read","input":{"file_path":"a.cs"}}]},
             {"role":"user","content":[{"type":"tool_result","tool_use_id":"read_1","content":"class A {}"}]}]
            """);
        var history = request["messages"]!.ToJsonString();
        var tools = request["tools"]!.ToJsonString();
        await provider.SendAsync(request, CancellationToken.None);
        var forwarded = transport.Bodies.Single();
        await Assert.That(forwarded["tool_choice"]!["type"]!.GetValue<string>()).IsEqualTo("auto");
        await Assert.That(forwarded["tool_choice"]!.AsObject().Count).IsEqualTo(1);
        await Assert.That(forwarded["thinking"] is null && forwarded["temperature"] is null && forwarded["top_p"] is null && forwarded["top_k"] is null).IsTrue();
        await Assert.That(forwarded["max_tokens"]!.GetValue<int>()).IsEqualTo(expected);
        await Assert.That(forwarded["messages"]!.ToJsonString()).IsEqualTo(history);
        await Assert.That(forwarded["tools"]!.ToJsonString()).IsEqualTo(tools);
    }

    [Test]
    public async Task RequestsWithoutToolsDoNotForwardToolChoice()
    {
        var transport = new Transport((_, model, _) => Task.FromResult(Success(model)));
        await using var provider = Create(transport);
        var request = Request();
        request.Remove("tools");
        request["tool_choice"] = new JsonObject { ["type"] = "any" };
        await provider.SendAsync(request, CancellationToken.None);
        await Assert.That(transport.Bodies.Single()["tool_choice"]).IsNull();
    }

    [Test]
    [Arguments("bash")]
    [Arguments("mcp_build")]
    [Arguments("server_type")]
    public async Task RejectsExecutableToolsBeforeCallingAnthropic(string tool)
    {
        var transport = new Transport((_, model, _) => Task.FromResult(Success(model)));
        await using var provider = Create(transport);
        await provider.StartAsync(CancellationToken.None);
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("x-api-key", provider.Token);
        var request = Request();
        var definition = new JsonObject { ["name"] = tool == "server_type" ? "file_read" : tool, ["input_schema"] = new JsonObject { ["type"] = "object" } };
        if (tool == "server_type") definition["type"] = "bash_20250124";
        request["tools"]!.AsArray().Add(definition);
        using var response = await client.PostAsJsonAsync(provider.Url + "/v1/messages", request);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(transport.Models.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("upstream-message")]
    [Arguments("unknown-error-type")]
    [Arguments("transport")]
    public async Task ProviderFailuresNeverExposeUpstreamCredentialsOrPromptMaterial(string failure)
    {
        const string sensitive = "private-key-and-prompt";
        var transport = new Transport((_, _, _) =>
        {
            if (failure == "transport") throw new HttpRequestException(sensitive);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = JsonContent.Create(new { error = new { type = failure == "unknown-error-type" ? sensitive : "authentication_error", message = sensitive } })
            });
        });
        var log = new CaptureLogger();
        await using var provider = new ReviewProvider(Options, log, "review", new HttpClient(transport), (_, _) => Task.CompletedTask);
        await provider.StartAsync(CancellationToken.None);
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("x-api-key", provider.Token);
        using var response = await client.PostAsJsonAsync(provider.Url + "/v1/messages", Request());
        var body = await response.Content.ReadAsStringAsync();
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(body.Contains(sensitive)).IsFalse();
        await Assert.That(provider.Failure!.Contains(sensitive)).IsFalse();
        await Assert.That(log.Messages.Any(message => message.Contains(sensitive))).IsFalse();
        await Assert.That(transport.Models.Count).IsEqualTo(30);
    }

    [Test]
    public async Task RetriesOpusAndResetsTheBudgetForTheNextStep()
    {
        var delay = new List<TimeSpan>();
        var log = new CaptureLogger();
        var transport = new Transport((attempt, model, _) => Task.FromResult(attempt <= 3
            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) : Success(model)));
        await using var provider = new ReviewProvider(Options, log, "TerraScale/widgets#9", new HttpClient(transport),
            (wait, _) => { delay.Add(wait); return Task.CompletedTask; });
        await provider.SendAsync(Request(), CancellationToken.None);
        await provider.SendAsync(Request(), CancellationToken.None);
        await Assert.That(string.Join(",", transport.Models)).IsEqualTo("claude-opus-5-5,claude-opus-5-5,claude-opus-5-5,claude-opus-5-5,claude-opus-5-5");
        await Assert.That(delay.Count).IsEqualTo(3);
        await Assert.That(log.Messages.Any(x => x.Contains("model claude-opus-5-5; attempts 4; retries 3; succeeded"))).IsTrue();
        await Assert.That(log.Messages.Any(x => x.Contains("step 2; model claude-opus-5-5; attempts 1; retries 0; succeeded"))).IsTrue();
        await Assert.That(provider.ModelsUsed).IsEqualTo("claude-opus-5-5");
    }

    [Test]
    public async Task SuccessiveLongStepsRetainTheirOwnRetryBudgets()
    {
        // Successive calls keep independent budgets even beyond the retired four-hour review deadline.
        var elapsed = TimeSpan.Zero;
        var failures = 12;
        var stepAttempt = 0;
        var log = new CaptureLogger();
        var transport = new Transport((_, model, _) =>
        {
            var failed = ++stepAttempt <= failures;
            elapsed += failed ? ReviewProvider.AttemptTimeout : TimeSpan.FromSeconds(1);
            return Task.FromResult(failed ? new HttpResponseMessage(HttpStatusCode.GatewayTimeout) : Success(model));
        });
        await using var provider = new ReviewProvider(Options, log, "review", new HttpClient(transport),
            (wait, _) => { elapsed += wait; return Task.CompletedTask; });
        await provider.SendAsync(Request(), CancellationToken.None);
        var firstCallElapsed = elapsed;
        failures = 25;
        stepAttempt = 0;
        await provider.SendAsync(Request(), CancellationToken.None);
        await Assert.That(firstCallElapsed < ReviewProvider.StepTimeout).IsTrue();
        await Assert.That(elapsed - firstCallElapsed < ReviewProvider.StepTimeout).IsTrue();
        await Assert.That(elapsed > ReviewProvider.StepTimeout).IsTrue();
        await Assert.That(elapsed > TimeSpan.FromHours(4)).IsTrue();
        await Assert.That(transport.Models.Count).IsEqualTo(39);
        await Assert.That(log.Messages.Any(x => x.Contains("step 2; model claude-opus-5-5; attempts 26; retries 25; succeeded"))).IsTrue();
        await Assert.That(provider.Failure).IsNull();
    }

    [Test]
    public async Task ThirtyTwentyMinuteCallsAndMaximumBackoffFitTheSdkDeadline()
    {
        var elapsed = TimeSpan.FromMinutes(20) * 30;
        for (var attempt = 1; attempt < 30; attempt++) elapsed += ReviewProvider.RetryDelay(attempt, 1);
        await Assert.That(ReviewProvider.AttemptTimeout).IsEqualTo(TimeSpan.FromMinutes(20));
        await Assert.That(elapsed < ReviewProvider.StepTimeout).IsTrue();
    }

    [Test]
    [Arguments(503)]
    [Arguments(401)]
    [Arguments(403)]
    [Arguments(400)]
    public async Task ThirtyOpusAttemptsCannotBeReopenedByOcr(int upstreamStatus)
    {
        var delays = 0;
        var transport = new Transport((_, _, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)upstreamStatus)
        { Content = JsonContent.Create(new { error = new { message = "upstream rejected" } }) }));
        await using var provider = new ReviewProvider(Options, NullLogger.Instance, "review", new HttpClient(transport),
            (_, _) => { delays++; return Task.CompletedTask; });
        await provider.StartAsync(CancellationToken.None);
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("Authorization", "Bearer " + provider.Token);
        for (var sdkAttempt = 0; sdkAttempt < 2; sdkAttempt++)
        {
            using var response = await client.PostAsJsonAsync(provider.Url + "/v1/messages", Request());
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That((await response.Content.ReadAsStringAsync()).Contains($"failed after 30 attempts: HTTP {upstreamStatus}: provider request failed")).IsTrue();
        }
        await Assert.That(transport.Models.Count).IsEqualTo(30);
        await Assert.That(delays).IsEqualTo(29);
        await Assert.That(transport.Models.All(x => x == "claude-opus-5-5")).IsTrue();
    }

    [Test]
    public async Task StreamingRequestsAreRejectedBeforeCallingAnthropic()
    {
        var transport = new Transport((_, model, _) => Task.FromResult(Success(model)));
        await using var provider = Create(transport);
        await provider.StartAsync(CancellationToken.None);
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("Authorization", "Bearer " + provider.Token);
        var body = Request();
        body["stream"] = true;
        using var streaming = await client.PostAsJsonAsync(provider.Url + "/v1/messages", body);
        await Assert.That(streaming.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await streaming.Content.ReadAsStringAsync()).Contains("only non-streaming requests")).IsTrue();
        await Assert.That(transport.Models.Count).IsEqualTo(0);
        using var buffered = await client.PostAsJsonAsync(provider.Url + "/v1/messages", Request());
        await Assert.That(buffered.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(transport.Models.Count).IsEqualTo(1);
    }

    [Test]
    [NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LargePromptsReachAnthropicWithKnownLengthOrChunkedBodies(bool chunked)
    {
        var transport = new Transport((_, model, _) => Task.FromResult(Success(model)));
        await using var provider = Create(transport);
        await provider.StartAsync(CancellationToken.None);
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("Authorization", "Bearer " + provider.Token);
        var body = Request();
        body["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = new string('x', 30_000_001) });
        using var request = new HttpRequestMessage(HttpMethod.Post, provider.Url + "/v1/messages")
        {
            Content = new PromptContent(Encoding.UTF8.GetBytes(body.ToJsonString()))
        };
        request.Content.Headers.ContentType = new("application/json");
        await Assert.That(request.Content.Headers.ContentLength!.Value).IsGreaterThan(30_000_000L);
        if (chunked) request.Headers.TransferEncodingChunked = true;
        using var response = await client.SendAsync(request);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(transport.Models.Single()).IsEqualTo("claude-opus-5-5");
        await Assert.That(provider.Failure).IsNull();
    }

    [Test]
    public async Task TheThirtiethAttemptCanStillSucceed()
    {
        var transport = new Transport((attempt, model, _) => Task.FromResult(attempt < 30
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : Success(model)));
        await using var provider = Create(transport);
        await provider.SendAsync(Request(), CancellationToken.None);
        await Assert.That(transport.Models.Count).IsEqualTo(30);
        await Assert.That(provider.Failure).IsNull();
    }

    [Test]
    [Arguments("429")]
    [Arguments("500")]
    [Arguments("503")]
    [Arguments("timeout")]
    [Arguments("connection")]
    [Arguments("empty")]
    [Arguments("malformed")]
    [Arguments("wrong-model")]
    [Arguments("missing-fields")]
    [Arguments("empty-text")]
    [Arguments("malformed-tool")]
    [Arguments("truncated")]
    [Arguments("thinking-only")]
    [Arguments("text-with-tool-stop")]
    [Arguments("tool-with-end-stop")]
    [Arguments("tool-with-sequence-stop")]
    public async Task ProviderFailuresAreRetried(string failure)
    {
        var transport = new Transport((attempt, model, _) =>
        {
            if (attempt > 1) return Task.FromResult(Success(model));
            if (failure == "timeout") throw new TaskCanceledException("provider timeout");
            if (failure == "connection") throw new HttpRequestException("connection lost");
            if (int.TryParse(failure, out var status)) return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status));
            var response = failure switch
            {
                "empty" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") },
                "malformed" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{invalid") },
                "wrong-model" => Success("forbidden-model"),
                "missing-fields" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") },
                "empty-text" => Success(model, [new { type = "text", text = " " }]),
                "malformed-tool" => Success(model, [new { type = "tool_use", id = "tool_1", name = "review", input = "invalid" }]),
                "truncated" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"type\":\"message\",\"stop_reason\":\"max_tokens\"}") },
                "text-with-tool-stop" => Success(model, stopReason: "tool_use"),
                "tool-with-end-stop" => Success(model, [new { type = "tool_use", id = "tool_1", name = "code_search", input = new { search_text = "failure" } }]),
                "tool-with-sequence-stop" => Success(model, [new { type = "tool_use", id = "tool_1", name = "code_search", input = new { search_text = "failure" } }], "stop_sequence"),
                _ => Success(model, [new { type = "thinking", thinking = "reasoning", signature = "signed" }])
            };
            return Task.FromResult(response);
        });
        await using var provider = Create(transport);
        await provider.SendAsync(Request(), CancellationToken.None);
        await Assert.That(transport.Models.Count).IsEqualTo(2);
        await Assert.That(provider.Failure).IsNull();
    }

    [Test]
    [Arguments("""{"type":"thinking","thinking":123,"signature":"signed"}""", false)]
    [Arguments("""{"type":"thinking","signature":"signed"}""", false)]
    [Arguments("""{"type":"thinking","thinking":"reasoning","signature":false}""", false)]
    [Arguments("""{"type":"thinking","thinking":"reasoning"}""", false)]
    [Arguments("""{"type":"thinking","thinking":"","signature":""}""", false)]
    [Arguments("""{"type":"redacted_thinking","data":null}""", false)]
    [Arguments("""{"type":"redacted_thinking","data":""}""", false)]
    [Arguments("""{"type":"redacted_thinking"}""", false)]
    [Arguments("""{"type":"thinking","thinking":"reasoning","signature":"signed"}""", true)]
    [Arguments("""{"type":"thinking","thinking":"","signature":"signed"}""", true)]
    [Arguments("""{"type":"redacted_thinking","data":"redacted"}""", true)]
    public async Task ThinkingBlocksAreValidatedEvenWithUsableText(string block, bool valid)
    {
        var transport = new Transport((attempt, model, _) => Task.FromResult(attempt == 1
            ? Success(model, [new { type = "text", text = "Reviewed." }, JsonSerializer.Deserialize<JsonElement>(block)])
            : Success(model)));
        await using var provider = Create(transport);
        await provider.SendAsync(Request(), CancellationToken.None);
        await Assert.That(transport.Models.Count).IsEqualTo(valid ? 1 : 2);
        await Assert.That(provider.Failure).IsNull();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ToolCallIdsMustBeUniqueWithinEachResponse(bool duplicate)
    {
        var transport = new Transport((attempt, model, _) => Task.FromResult(Success(model,
            [new { type = "tool_use", id = "tool_1", name = "code_search", input = new { search_text = "first" } },
             new { type = "tool_use", id = duplicate && attempt == 1 ? "tool_1" : "tool_2", name = "code_search", input = new { search_text = "second" } }], "tool_use")));
        var log = new CaptureLogger();
        await using var provider = new ReviewProvider(Options, log, "review", new HttpClient(transport), (_, _) => Task.CompletedTask);
        await provider.SendAsync(Request(), CancellationToken.None);
        await Assert.That(transport.Models.Count).IsEqualTo(duplicate ? 2 : 1);
        await Assert.That(log.Messages.Any(x => x.Contains("duplicate tool-call IDs"))).IsEqualTo(duplicate);
        await provider.SendAsync(Request(), CancellationToken.None);
        await Assert.That(transport.Models.Count).IsEqualTo(duplicate ? 3 : 2);
        await Assert.That(provider.Failure).IsNull();
    }

    [Test]
    public async Task AcceptsAnthropicToolCallsAndDoesNotRetryThem()
    {
        var transport = new Transport((_, model, _) => Task.FromResult(Success(model,
            [new { type = "text", text = "" }, new { type = "tool_use", id = "tool_1", name = "code_search", input = new { search_text = "failure" } }], "tool_use")));
        await using var provider = Create(transport);
        var result = await provider.SendAsync(Request(), CancellationToken.None);
        await Assert.That(result.Contains("code_search")).IsTrue();
        await Assert.That(transport.Models.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments("unknown_tool", "{}")]
    [Arguments("code_search", "{}")]
    [Arguments("code_search", "{\"search_text\":null}")]
    [Arguments("code_search", "{\"search_text\":\"x\",\"case_sensitive\":\"true\"}")]
    [Arguments("code_search", "{\"search_text\":\"x\",\"file_patterns\":\"a.cs\"}")]
    [Arguments("code_search", "{\"search_text\":\"x\",\"file_patterns\":[1]}")]
    [Arguments("file_read", "{\"file_path\":\"a.cs\",\"start_line\":1.5}")]
    [Arguments("file_read", "{\"file_path\":\"a.cs\",\"start_line\":\"1\"}")]
    [Arguments("task_done", "{\"state\":\"UNKNOWN\"}")]
    [Arguments("code_comment", "{\"comments\":[{}]}")]
    [Arguments("code_comment", "{\"comments\":[{\"content\":\"bug\",\"existing_code\":\"x\",\"category\":\"bug\",\"severity\":\"blocker\",\"path\":\"a.cs\"}]}")]
    [Arguments("code_comment", "{\"comments\":[{\"content\":\"bug\",\"existing_code\":\"x\",\"category\":\"bug\",\"severity\":\"high\",\"path\":false}]}")]
    public async Task UnknownToolsAndSchemaInvalidArgumentsAreRetried(string name, string arguments)
    {
        var transport = new Transport((attempt, model, _) => Task.FromResult(attempt == 1
            ? Success(model, [new { type = "tool_use", id = "tool_1", name, input = JsonSerializer.Deserialize<JsonElement>(arguments) }], "tool_use")
            : Success(model)));
        var log = new CaptureLogger();
        await using var provider = new ReviewProvider(Options, log, "review", new HttpClient(transport), (_, _) => Task.CompletedTask);
        await provider.SendAsync(Request(), CancellationToken.None);
        await Assert.That(transport.Models.Count).IsEqualTo(2);
        await Assert.That(log.Messages.Any(x => x.Contains(name == "unknown_tool" ? "tool not declared" : "schema-invalid tool arguments"))).IsTrue();
        await Assert.That(provider.Failure).IsNull();
    }

    [Test]
    public async Task ToolCallsWithoutDeclaredToolsAreRetried()
    {
        var transport = new Transport((attempt, model, _) => Task.FromResult(attempt == 1
            ? Success(model, [new { type = "tool_use", id = "tool_1", name = "code_search", input = new { search_text = "failure" } }], "tool_use")
            : Success(model)));
        await using var provider = Create(transport);
        var request = Request();
        request.Remove("tools");
        await provider.SendAsync(request, CancellationToken.None);
        await Assert.That(transport.Models.Count).IsEqualTo(2);
    }

    [Test]
    [Arguments("code_search", "{\"search_text\":\"x\",\"file_patterns\":[\"*.cs\"],\"case_sensitive\":false}")]
    [Arguments("file_read", "{\"file_path\":\"a.cs\",\"start_line\":1.0,\"end_line\":10}")]
    [Arguments("task_done", "{\"state\":\"DONE\"}")]
    [Arguments("code_comment", "{\"comments\":[{\"content\":\"bug\",\"existing_code\":\"x\",\"category\":\"bug\",\"severity\":\"high\",\"path\":\"a.cs\"}]}")]
    public async Task SchemaValidToolArgumentsDoNotRetry(string name, string arguments)
    {
        var transport = new Transport((_, model, _) => Task.FromResult(Success(model,
            [new { type = "tool_use", id = "tool_1", name, input = JsonSerializer.Deserialize<JsonElement>(arguments) }], "tool_use")));
        await using var provider = Create(transport);
        await provider.SendAsync(Request(), CancellationToken.None);
        await Assert.That(transport.Models.Count).IsEqualTo(1);
    }

    [Test]
    public async Task BackoffDoublesWithJitterAndNeverExceedsSixtySeconds()
    {
        await Assert.That(ReviewProvider.RetryDelay(1, 0)).IsEqualTo(TimeSpan.FromMilliseconds(500));
        await Assert.That(ReviewProvider.RetryDelay(1, 1)).IsEqualTo(TimeSpan.FromSeconds(1));
        await Assert.That(ReviewProvider.RetryDelay(3, 0)).IsEqualTo(TimeSpan.FromSeconds(2));
        await Assert.That(ReviewProvider.RetryDelay(3, 1)).IsEqualTo(TimeSpan.FromSeconds(4));
        for (var failure = 1; failure <= 30; failure++)
        {
            var low = ReviewProvider.RetryDelay(failure, 0);
            var high = ReviewProvider.RetryDelay(failure, 1);
            await Assert.That(low).IsEqualTo(high / 2);
            await Assert.That(high <= TimeSpan.FromSeconds(60)).IsTrue();
        }
        await Assert.That(ReviewProvider.RetryDelay(int.MaxValue, 1)).IsEqualTo(TimeSpan.FromSeconds(60));
    }

    [Test]
    public async Task CancellationInterruptsBackoffWithoutAnotherAttempt()
    {
        var delaying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new Transport((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)));
        await using var provider = new ReviewProvider(Options, NullLogger.Instance, "review", new HttpClient(transport),
            (_, ct) => { delaying.TrySetResult(); return Task.Delay(Timeout.InfiniteTimeSpan, ct); });
        using var cancellation = new CancellationTokenSource();
        var run = provider.SendAsync(Request(), cancellation.Token);
        await delaying.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var cancelled = false;
        try { await run; } catch (OperationCanceledException) { cancelled = true; }
        await Assert.That(cancelled).IsTrue();
        await Assert.That(transport.Models.Count).IsEqualTo(1);
    }

    [Test]
    public async Task CancellationInterruptsAnActiveProviderCall()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new Transport(async (_, _, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Success("claude-opus-5-5");
        });
        await using var provider = Create(transport);
        using var cancellation = new CancellationTokenSource();
        var run = provider.SendAsync(Request(), cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var cancelled = false;
        try { await run; } catch (OperationCanceledException) { cancelled = true; }
        await Assert.That(cancelled).IsTrue();
        await Assert.That(transport.Models.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReviewCancellationOrDisposalStopsAnActiveLocalHttpCall(bool dispose)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new Transport(async (_, model, ct) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            finally { stopped.TrySetResult(); }
            return Success(model);
        });
        using var review = new CancellationTokenSource();
        var provider = Create(transport);
        using var client = new HttpClient();
        Task? disposal = null;
        try
        {
            await provider.StartAsync(review.Token);
            client.DefaultRequestHeaders.Add("x-api-key", provider.Token);
            var request = client.PostAsJsonAsync(provider.Url + "/v1/messages", Request());
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (dispose) disposal = provider.DisposeAsync().AsTask();
            else review.Cancel();
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                using var response = await request.WaitAsync(TimeSpan.FromSeconds(5));
                await Assert.That(response.IsSuccessStatusCode).IsFalse();
            }
            catch (HttpRequestException) when (dispose) { } // Disposing the listener can close its client connection.
            await Assert.That(transport.Models.Count).IsEqualTo(1);
            await Assert.That(provider.Failure).IsNull();
        }
        finally
        {
            client.Dispose();
            await (disposal ?? provider.DisposeAsync().AsTask());
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LocalDisconnectCannotReopenAPartlySpentRetryBudget(bool backoff)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var log = new CaptureLogger();
        var transport = new Transport(async (attempt, _, ct) =>
        {
            if (!backoff && attempt == 29)
            {
                started.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                finally { stopped.TrySetResult(); }
            }
            return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        });
        await using var provider = new ReviewProvider(Options, log, "review", new HttpClient(transport), async (_, ct) =>
        {
            if (backoff && transport.Models.Count == 29)
            {
                started.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                finally { stopped.TrySetResult(); }
            }
        });
        await provider.StartAsync(CancellationToken.None);
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("x-api-key", provider.Token);
        using var disconnect = new CancellationTokenSource();
        var request = client.PostAsJsonAsync(provider.Url + "/v1/messages", Request(), disconnect.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        disconnect.Cancel();
        var cancelled = false;
        try { using var response = await request; } catch (OperationCanceledException) { cancelled = true; }
        await Assert.That(cancelled).IsTrue();
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var retry = await client.PostAsJsonAsync(provider.Url + "/v1/messages", Request());
        await Assert.That(retry.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await retry.Content.ReadAsStringAsync()).Contains("OCR disconnected during a model call");
        await Assert.That(provider.Failure).Contains("preserve its retry budget");
        await Assert.That(transport.Models.Count).IsEqualTo(29);
        await Assert.That(provider.Lifetime.IsCancellationRequested).IsFalse();
        await Assert.That(log.Messages.Any(x => x.Contains("review review; step 1; OCR disconnected"))).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DisconnectDuringResponseDeliveryCannotReopenTheRetryBudget(bool reviewCancellation)
    {
        var transport = new Transport((attempt, model, _) => Task.FromResult(attempt < 30
            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            : Success(model, [new { type = "text", text = new string('x', 32 * 1024 * 1024) }])));
        await using var provider = Create(transport);
        using var review = new CancellationTokenSource();
        await provider.StartAsync(review.Token);
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("x-api-key", provider.Token);
        using var request = new HttpRequestMessage(HttpMethod.Post, provider.Url + "/v1/messages") { Content = JsonContent.Create(Request()) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content.Headers.ContentLength > 32 * 1024 * 1024).IsTrue();
        await Assert.That(transport.Models.Count).IsEqualTo(30);
        if (reviewCancellation) review.Cancel();
        // Disposing an unread body closes the local connection while the large response is still being written.
        response.Dispose();
        using var retry = await client.PostAsJsonAsync(provider.Url + "/v1/messages", Request()).WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(retry.IsSuccessStatusCode).IsFalse();
        await Assert.That(transport.Models.Count).IsEqualTo(30);
        if (reviewCancellation) await Assert.That(provider.Failure).IsNull();
        else
        {
            await Assert.That(retry.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(await retry.Content.ReadAsStringAsync()).Contains("OCR response delivery failed");
            await Assert.That(provider.Failure).Contains("preserve its retry budget");
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task InactivityWatchdogPausesForActiveCallsAndRetryBackoff(bool backoff)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new ManualTimeProvider();
        var transport = new Transport(async (attempt, model, ct) =>
        {
            if (backoff && attempt == 1) return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            if (!backoff) { started.TrySetResult(); await release.Task.WaitAsync(ct); }
            return Success(model);
        });
        await using var provider = new ReviewProvider(Options, NullLogger.Instance, "review", new HttpClient(transport),
            async (_, ct) => { started.TrySetResult(); await release.Task.WaitAsync(ct); }, clock);
        await provider.StartAsync(CancellationToken.None);
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("x-api-key", provider.Token);
        var request = client.PostAsJsonAsync(provider.Url + "/v1/messages", Request());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(ReviewProvider.StepTimeout);
        await Assert.That(provider.Lifetime.IsCancellationRequested).IsFalse();
        await Assert.That(request.IsCompleted).IsFalse();
        release.TrySetResult();
        using var response = await request.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(transport.Models.Count).IsEqualTo(backoff ? 2 : 1);
        clock.Advance(ReviewProvider.InactivityTimeout - TimeSpan.FromTicks(1));
        await Assert.That(provider.Lifetime.IsCancellationRequested).IsFalse();
        clock.Advance(TimeSpan.FromTicks(1));
        await Assert.That(provider.Lifetime.IsCancellationRequested).IsTrue();
        await Assert.That(provider.Failure).IsNull();
    }

    [Test]
    public async Task EachReviewSerializesModelCallsAndAuthenticatesItsLocalEndpoint()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new Transport(async (attempt, model, ct) =>
        {
            if (attempt == 1) { started.TrySetResult(); await release.Task.WaitAsync(ct); }
            return Success(model);
        });
        await using var provider = Create(transport);
        await provider.StartAsync(CancellationToken.None);
        using var client = new HttpClient();
        using var unauthenticated = await client.PostAsJsonAsync(provider.Url + "/v1/messages", Request());
        await Assert.That(unauthenticated.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(transport.Models.Count).IsEqualTo(0);
        client.DefaultRequestHeaders.Add("x-api-key", provider.Token);
        var first = client.PostAsJsonAsync(provider.Url + "/v1/messages", Request());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = provider.SendAsync(Request(), CancellationToken.None);
        try
        {
            await Assert.That(transport.Models.Count).IsEqualTo(1);
            await Assert.That(second.IsCompleted).IsFalse();
        }
        finally { release.TrySetResult(); }
        using var response = await first;
        await second;
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(transport.Models.Count).IsEqualTo(2);
    }

    private static ReviewProvider Create(Transport transport) => new(Options, NullLogger.Instance, "review", new HttpClient(transport), (_, _) => Task.CompletedTask);

    private sealed class PromptContent(byte[] payload) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = payload.Length; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
        {
            // Keep uploads in bounded writes instead of one 30 MB socket write.
            for (var offset = 0; offset < payload.Length; offset += 64 * 1024)
                await stream.WriteAsync(payload.AsMemory(offset, Math.Min(64 * 1024, payload.Length - offset)), ct);
        }
    }

    private sealed class Transport(Func<int, string, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal List<string> Models { get; } = [];
        internal List<JsonObject> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsoluteUri != "https://anthropic.test/v1/messages"
                || request.Headers.Authorization is not null
                || request.Headers.GetValues("x-api-key").Single() != "anthropic-secret"
                || request.Headers.GetValues("anthropic-version").Single() != "2023-06-01") throw new InvalidOperationException("incorrect Anthropic endpoint or credentials");
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Bodies.Add(JsonNode.Parse(document.RootElement.GetRawText())!.AsObject());
            if (document.RootElement.GetProperty("stream").GetBoolean()) throw new InvalidOperationException("OCR must use buffered responses");
            var model = document.RootElement.GetProperty("model").GetString()!;
            Models.Add(model);
            return await send(Models.Count, model, ct);
        }
    }

    private sealed class CaptureLogger : ILogger
    {
        internal List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
