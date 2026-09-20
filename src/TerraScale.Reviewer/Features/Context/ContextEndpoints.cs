using System.Security.Cryptography;
using System.Text;
using TerraScale.Reviewer.Features.Configuration;

namespace TerraScale.Reviewer.Features.Context;

public static class ContextEndpoints
{
    internal static IResult DeadLetters(HttpRequest request, ContextStore store, ReviewerOptions options)
    {
        if (!Authorized(request, options.OperatorToken)) return Results.Unauthorized();
        return Results.Ok(new { jobs = store.DeadLetterJobs() });
    }

    internal static async Task<IResult> ReplayDeadLetterAsync(HttpRequest request, ContextStore store,
        ContextIndexer indexer, ReviewerOptions options, CancellationToken cancellationToken)
    {
        if (!Authorized(request, options.OperatorToken)) return Results.Unauthorized();
        var body = await request.ReadFromJsonAsync<ReplayRequest>(cancellationToken);
        if (body is null || string.IsNullOrWhiteSpace(body.Repository) || string.IsNullOrWhiteSpace(body.Commit))
            return Results.BadRequest("repository and commit are required");
        var separator = body.Repository.IndexOf('/');
        if (separator <= 0 || separator == body.Repository.Length - 1) return Results.BadRequest("repository must be owner/name");
        if (!store.ReplayDeadLetter(body.Repository, body.Commit)) return Results.NotFound();
        var accepted = indexer.Enqueue(body.Repository[..separator], body.Repository[(separator + 1)..], body.Commit);
        return accepted ? Results.Accepted() : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    public static async Task<IResult> SearchAsync(HttpRequest request, ContextIndexer indexer,
        ReviewerOptions options, CancellationToken cancellationToken)
    {
        if (!Authorized(request, options.ContextApiToken))
            return Results.Unauthorized();

        var body = await request.ReadFromJsonAsync<ContextSearchRequest>(cancellationToken);
        return body is null
            ? Results.BadRequest("invalid context search request")
            : Results.Ok(await indexer.SearchAsync(body, cancellationToken));
    }

    private static bool Authorized(HttpRequest request, string token) =>
        !string.IsNullOrWhiteSpace(token) &&
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(request.Headers.Authorization.ToString()),
            Encoding.UTF8.GetBytes("Bearer " + token));

    private sealed record ReplayRequest(string Repository, string Commit);
}
