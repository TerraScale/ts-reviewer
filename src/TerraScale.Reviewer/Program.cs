using TerraScale.Reviewer.Features.Configuration;
using TerraScale.Reviewer.Features.Context;
using TerraScale.Reviewer.Features.Forgejo;
using TerraScale.Reviewer.Features.Review;
using TerraScale.Reviewer.Features.Webhooks;

var options = ReviewerOptions.Load(args);
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(options.ListenUrl);
builder.Services.AddSingleton(options);
builder.Services.AddHttpClient<ForgejoClient>();
builder.Services.AddHttpClient<ReviewEngine>();
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
app.MapPost("/context/search", async (HttpRequest request, ContextIndexer indexer, ReviewerOptions reviewerOptions, CancellationToken cancellationToken) =>
{
    if (!string.IsNullOrWhiteSpace(reviewerOptions.ContextApiToken) &&
        !string.Equals(request.Headers.Authorization.ToString(), "Bearer " + reviewerOptions.ContextApiToken, StringComparison.Ordinal))
        return Results.Unauthorized();
    var body = await request.ReadFromJsonAsync<ContextSearchRequest>(cancellationToken);
    return body is null ? Results.BadRequest("invalid context search request") : Results.Ok(await indexer.SearchAsync(body, cancellationToken));
});
app.MapGet("/context/healthz", async (ContextStore store, CancellationToken cancellationToken) => Results.Ok(new { status = "ok" }));
app.Lifetime.ApplicationStarted.Register(() =>
    app.Logger.LogInformation("ts-reviewer listening on {Address}; model {Model}; max_concurrent {MaxConcurrent}",
        options.ListenUrl, options.Model, options.MaxConcurrent));
await app.RunAsync();
