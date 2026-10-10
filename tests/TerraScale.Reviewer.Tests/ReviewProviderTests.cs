using System.Net;
using System.Net.Http.Json;
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
    private static readonly ReviewerOptions Options = new("http://+:8080", "https://forgejo.test", "forgejo",
        "https://router.test/v1", "proxy-secret", "TerraScale", "bot", TimeSpan.FromHours(4), "webhook", "context", "unused", 100);

    private static JsonObject Request() => new() { ["model"] = "ignored", ["messages"] = new JsonArray(), ["stream"] = true };

    private static HttpResponseMessage Success(string model, object[]? content = null) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new
        {
            id = "msg_1",
            type = "message",
            role = "assistant",
            model,
            stop_reason = "end_turn",
            usage = new { input_tokens = 10, output_tokens = 3 },
            content = content ?? [new { type = "text", text = "Reviewed." }]
        })
    };

    [Test]
    public async Task RetriesThroughFlashAndResetsTheBudgetForTheNextStep()
    {
        var delay = new List<TimeSpan>();
        var log = new CaptureLogger();
        var transport = new Transport((attempt, model, _) => Task.FromResult(attempt <= 3
            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) : Success(model)));
        await using var provider = new ReviewProvider(Options, log, "TerraScale/widgets#9", new HttpClient(transport),
            (wait, _) => { delay.Add(wait); return Task.CompletedTask; });
        await provider.SendAsync(Request(), CancellationToken.None);
        await provider.SendAsync(Request(), CancellationToken.None);
        await Assert.That(string.Join(",", transport.Models)).IsEqualTo("glm-5.3,glm-5.3,glm-5.3,glm-5.3-flash,glm-5.3");
        await Assert.That(delay.Count).IsEqualTo(3);
        await Assert.That(log.Messages.Any(x => x.Contains("model glm-5.3-flash; attempts 4; retries 3; succeeded"))).IsTrue();
        await Assert.That(log.Messages.Any(x => x.Contains("step 2; model glm-5.3; attempts 1; retries 0; succeeded"))).IsTrue();
        await Assert.That(provider.ModelsUsed).IsEqualTo("glm-5.3 → glm-5.3-flash");
    }

    [Test]
    public async Task ThirtyAttemptsAreSharedAcrossModelsAndCannotBeReopenedByOcr()
    {
        var delays = 0;
        var transport = new Transport((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        { Content = JsonContent.Create(new { error = new { message = "upstream unavailable" } }) }));
        await using var provider = new ReviewProvider(Options, NullLogger.Instance, "review", new HttpClient(transport),
            (_, _) => { delays++; return Task.CompletedTask; });
        await provider.StartAsync(CancellationToken.None);
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("Authorization", "Bearer " + provider.Token);
        for (var sdkAttempt = 0; sdkAttempt < 2; sdkAttempt++)
        {
            using var response = await client.PostAsJsonAsync(provider.Url + "/v1/messages", Request());
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That((await response.Content.ReadAsStringAsync()).Contains("failed after 30 attempts: HTTP 503: upstream unavailable")).IsTrue();
        }
        await Assert.That(transport.Models.Count).IsEqualTo(30);
        await Assert.That(delays).IsEqualTo(29);
        await Assert.That(transport.Models.Count(x => x == "glm-5.3")).IsEqualTo(15);
        await Assert.That(transport.Models.Count(x => x == "glm-5.3-flash")).IsEqualTo(15);
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
                _ => Success(model, [new { type = "thinking", thinking = "reasoning" }])
            };
            return Task.FromResult(response);
        });
        await using var provider = Create(transport);
        await provider.SendAsync(Request(), CancellationToken.None);
        await Assert.That(transport.Models.Count).IsEqualTo(2);
        await Assert.That(provider.Failure).IsNull();
    }

    [Test]
    public async Task AcceptsAnthropicToolCallsAndDoesNotRetryThem()
    {
        var transport = new Transport((_, model, _) => Task.FromResult(Success(model,
            [new { type = "text", text = "" }, new { type = "tool_use", id = "tool_1", name = "code_search", input = new { query = "failure" } }])));
        await using var provider = Create(transport);
        var result = await provider.SendAsync(Request(), CancellationToken.None);
        await Assert.That(result.Contains("code_search")).IsTrue();
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
            return Success("glm-5.3");
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

    private sealed class Transport(Func<int, string, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal List<string> Models { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsoluteUri != "https://router.test/v1/messages"
                || request.Headers.Authorization?.ToString() != "Bearer proxy-secret") throw new InvalidOperationException("incorrect router endpoint or credentials");
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
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
