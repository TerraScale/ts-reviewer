using System.Text.Json.Serialization;

namespace TerraScale.Reviewer.Features.Forgejo;

public sealed record PullRequest([property: JsonPropertyName("number")] int Number, [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("body")] string Body, [property: JsonPropertyName("base")] Branch Base,
    [property: JsonPropertyName("head")] Branch Head);
public sealed record Branch([property: JsonPropertyName("sha")] string Sha, [property: JsonPropertyName("ref")] string Ref,
    [property: JsonPropertyName("repo")] Repository Repo);
public sealed record Repository([property: JsonPropertyName("owner")] Owner Owner, [property: JsonPropertyName("name")] string Name);
public sealed record Owner([property: JsonPropertyName("login")] string Login);
public sealed record ChangedFile([property: JsonPropertyName("filename")] string Filename, [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("patch")] string Patch, [property: JsonPropertyName("additions")] int Additions,
    [property: JsonPropertyName("deletions")] int Deletions);
public sealed record IssueComment([property: JsonPropertyName("id")] long Id, [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("user")] Owner User);
public sealed record ReviewComment([property: JsonPropertyName("body")] string Body, [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("new_position")] int NewPosition, [property: JsonPropertyName("old_position")] int OldPosition = 0,
    [property: JsonPropertyName("extra_lines_count")] int ExtraLines = 0);
public sealed record PullReview([property: JsonPropertyName("body")] string Body, [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("commit_id")] string CommitId, [property: JsonPropertyName("comments")] IReadOnlyList<ReviewComment>? Comments = null);
public sealed record ForgejoIssue([property: JsonPropertyName("number")] int Number, [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("body")] string Body);
public sealed record CommitStatus([property: JsonPropertyName("state")] string State, [property: JsonPropertyName("target_url")] string? TargetUrl,
    [property: JsonPropertyName("description")] string Description, [property: JsonPropertyName("context")] string Context);
