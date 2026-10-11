using System.Text.Json.Serialization;

namespace TerraScale.Reviewer.Features.GitHub;

public sealed record PullRequest([property: JsonPropertyName("number")] int Number, [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("body")] string Body, [property: JsonPropertyName("base")] Branch Base,
    [property: JsonPropertyName("head")] Branch Head, [property: JsonPropertyName("state")] string State = "open");
public sealed record Branch([property: JsonPropertyName("sha")] string Sha, [property: JsonPropertyName("ref")] string Ref,
    [property: JsonPropertyName("repo")] Repository Repo);
public sealed record Repository([property: JsonPropertyName("owner")] Owner Owner, [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("archived")] bool Archived = false);
public sealed record Owner([property: JsonPropertyName("login")] string Login, [property: JsonPropertyName("type")] string? Type = null);
public sealed record ChangedFile([property: JsonPropertyName("filename")] string Filename, [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("patch")] string Patch, [property: JsonPropertyName("additions")] int Additions,
    [property: JsonPropertyName("deletions")] int Deletions);
public sealed record IssueComment([property: JsonPropertyName("id")] long Id, [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("user")] Owner User);
public sealed record ReviewComment([property: JsonPropertyName("body")] string Body, [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("line")] int Line, [property: JsonPropertyName("side")] string Side = "RIGHT");
public sealed record PullReview([property: JsonPropertyName("body")] string Body, [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("commit_id")] string CommitId, [property: JsonPropertyName("comments")] IReadOnlyList<ReviewComment>? Comments = null);
public sealed record CommitStatus([property: JsonPropertyName("state")] string State, [property: JsonPropertyName("target_url")] string? TargetUrl,
    [property: JsonPropertyName("description")] string Description, [property: JsonPropertyName("context")] string Context);
public sealed record InstallationRepositories([property: JsonPropertyName("repositories")] IReadOnlyList<Repository>? Repositories);

/// <summary>Latest status per context for one GitHub commit.</summary>
public sealed record CombinedStatus([property: JsonPropertyName("statuses")] IReadOnlyList<ContextStatus>? Statuses);
public sealed record ContextStatus([property: JsonPropertyName("context")] string Context, [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("description")] string? Description);

public sealed record RepositoryTreeEntry([property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("type")] string Type, [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sha")] string Sha);

public sealed record RepositoryTreeResponse([property: JsonPropertyName("tree")] IReadOnlyList<RepositoryTreeEntry> Tree,
    [property: JsonPropertyName("truncated")] bool Truncated = false);

public sealed record RepositoryContent([property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("encoding")] string? Encoding, [property: JsonPropertyName("type")] string Type);
