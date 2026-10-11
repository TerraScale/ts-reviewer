using TerraScale.Reviewer.Features.Configuration;
using TerraScale.Reviewer.Features.Context;
using TerraScale.Reviewer.Features.GitHub;
using TerraScale.Reviewer.Features.Review;
using TerraScale.Reviewer.Features.Webhooks;
using System.Security.Cryptography;

var options = ReviewerOptions.Load(args);
try
{
    using var key = RSA.Create();
    key.ImportFromPem(File.ReadAllText(options.GitHubPrivateKeyPath));
    if (key.KeySize < 2048) throw new CryptographicException();
    _ = key.ExportParameters(true);
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or CryptographicException)
{
    throw new InvalidOperationException("GitHub App private key must be a readable RSA private PEM of at least 2048 bits");
}
if (args.Contains("--validate-configuration", StringComparer.Ordinal))
{
    Console.WriteLine("ts-reviewer configuration valid; no services or model calls started");
    return;
}
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(options.ListenUrl);
// No per-request framework traces: health probes and GitHub API calls would
// otherwise rotate the container log within minutes and bury review outcomes.
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(console =>
{
    console.SingleLine = true;
    console.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
});
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Services.AddSingleton(options);
builder.Services.AddHttpClient<GitHubClient>()
    .RemoveAllLoggers() // Archive redirects can contain temporary credentials in their query.
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<ReviewEngine>();
builder.Services.AddSingleton<PullRequestReviewer>();
builder.Services.AddSingleton<ReviewQueue>();
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<ReviewQueue>());
builder.Services.AddHostedService<ReviewRecovery>();
builder.Services.AddSingleton<ContextEmbedder>();
builder.Services.AddSingleton<ContextStore>();
builder.Services.AddSingleton<ContextIndexer>(serviceProvider => new ContextIndexer(
    serviceProvider.GetRequiredService<GitHubClient>(),
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
    app.Logger.LogInformation("ts-reviewer listening on {Address}; model {Model}; max_concurrent {MaxConcurrent}; file_groups 1; max_attempts {MaxAttempts}",
        options.ListenUrl, ReviewProvider.PrimaryModel, ReviewQueue.WorkerCount, ReviewProvider.MaxAttempts));
await app.RunAsync();
