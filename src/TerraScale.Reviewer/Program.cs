using Microsoft.Extensions.Logging.Console;
using TerraScale.Reviewer.Features.Configuration;
using TerraScale.Reviewer.Features.Context;
using TerraScale.Reviewer.Features.Forgejo;
using TerraScale.Reviewer.Features.Review;
using TerraScale.Reviewer.Features.Webhooks;

var options = ReviewerOptions.Load(args);
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(options.ListenUrl);
// One timestamped line per event, without ANSI colors, and no per-request
// framework traces: health probes and Forgejo API calls otherwise rotate the
// container console log within minutes and bury review outcomes.
builder.Logging.AddSimpleConsole(console =>
{
    console.SingleLine = true;
    console.UseUtcTimestamp = true;
    console.TimestampFormat = "yyyy-MM-ddTHH:mm:ssZ ";
    console.ColorBehavior = LoggerColorBehavior.Disabled;
});
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
builder.Services.AddSingleton(options);
builder.Services.AddHttpClient<ForgejoClient>();
builder.Services.AddSingleton<ReviewEngine>();
builder.Services.AddSingleton<PullRequestReviewer>();
builder.Services.AddSingleton<ReviewQueue>();
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<ReviewQueue>());
builder.Services.AddHostedService<ReviewRecovery>();
builder.Services.AddSingleton<ContextEmbedder>();
builder.Services.AddSingleton<ContextStore>();
builder.Services.AddSingleton<ContextIndexer>(serviceProvider => new ContextIndexer(
    serviceProvider.GetRequiredService<ForgejoClient>(),
    serviceProvider.GetRequiredService<ContextStore>(),
    serviceProvider.GetRequiredService<ContextEmbedder>(),
    serviceProvider.GetRequiredService<ReviewerOptions>(),
    serviceProvider.GetRequiredService<ILogger<ContextIndexer>>()));
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<ContextIndexer>());
builder.Services.AddSingleton<WebhookHandler>();

var app = builder.Build();
app.MapGet("/healthz", () => Results.Ok());
app.MapPost("/webhook", (HttpRequest request, WebhookHandler handler, CancellationToken cancellationToken) =>
    handler.HandleAsync(request, cancellationToken));
app.MapPost("/context/search", ContextEndpoints.SearchAsync);
app.MapGet("/context/healthz", async (ContextStore store, CancellationToken cancellationToken) => Results.Ok(new { status = "ok" }));
app.MapGet("/admin/context/dead-letter", ContextEndpoints.DeadLetters);
app.MapPost("/admin/context/dead-letter/replay", ContextEndpoints.ReplayDeadLetterAsync);
app.Lifetime.ApplicationStarted.Register(() =>
    app.Logger.LogInformation("ts-reviewer listening on {Address}; model {Model}; max_concurrent {MaxConcurrent}; ocr_concurrency {OcrConcurrency}",
        options.ListenUrl, options.Model, options.MaxConcurrent, options.OcrConcurrency));
await app.RunAsync();
