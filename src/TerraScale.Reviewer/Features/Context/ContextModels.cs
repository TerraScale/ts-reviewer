using System.Text.Json.Serialization;

namespace TerraScale.Reviewer.Features.Context;

public sealed record ContextSearchRequest(
    [property: JsonPropertyName("repository")] string Repository,
    [property: JsonPropertyName("commit")] string Commit,
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("top_k")] int TopK = 8);

public sealed record ContextHit(string Repository, string Commit, string Path, int StartLine, int EndLine,
    string Symbol, string Content, double Score);

public sealed record ContextSearchResponse(IReadOnlyList<ContextHit> Results, bool Indexed);

internal sealed record IndexRequest(string Owner, string Name, string Commit);

internal sealed record CodeChunk(string Path, int StartLine, int EndLine, string Symbol, string Content);

internal sealed record IndexJob(string Owner, string Name, string Commit);

internal sealed record IndexJobStatus(int Attempts, long NextAttemptAt, string? LastError, bool DeadLettered);

public sealed record IndexDeadLetter(string Repository, string Commit, int Attempts, long EnqueuedAt,
    string? LastError, long DeadAt);
