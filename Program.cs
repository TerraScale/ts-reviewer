using TerraScale.TsReviewer.Features.Configuration;
using TerraScale.TsReviewer.Features.Forgejo;
using TerraScale.TsReviewer.Features.Review;
using TerraScale.TsReviewer.Features.Webhooks;

var options = ReviewerOptions.Load(args);
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(options.ListenUrl);
builder.Services.AddSingleton(options);
builder.Services.AddHttpClient<ForgejoClient>();
builder.Services.AddHttpClient<ReviewEngine>();
builder.Services.AddSingleton<WebhookHandler>();

var app = builder.Build();
app.MapGet("/healthz", () => Results.Ok());
app.MapPost("/webhook", (HttpRequest request, WebhookHandler handler, CancellationToken cancellationToken) =>
    handler.HandleAsync(request, cancellationToken));
app.Lifetime.ApplicationStarted.Register(() =>
    app.Logger.LogInformation("ts-reviewer listening on {Address}; model {Model}; max_concurrent {MaxConcurrent}",
        options.ListenUrl, options.Model, options.MaxConcurrent));
await app.RunAsync();
