using System.Text.Json.Serialization;
using TerraScale.Reviewer.Features.Forgejo;

namespace TerraScale.Reviewer.Features.Review;

public sealed record Finding(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("fix")] string Fix,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("line")] int Line);

public sealed record ReviewResult
{
    [JsonPropertyName("summary")]
    public string Summary { get; init; } = string.Empty;

    [JsonPropertyName("findings")]
    public IReadOnlyList<Finding> Findings { get; init; } = [];

    public string? Model { get; init; }

    public IReadOnlyList<ChangedFile> Files { get; init; } = [];

    public ReviewResult() { }
    public ReviewResult(string summary, IReadOnlyList<Finding> findings) => (Summary, Findings) = (summary, findings);
}
