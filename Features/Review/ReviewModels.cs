using System.Text.Json.Serialization;

namespace TerraScale.Reviewer.Features.Review;

public sealed record Finding(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("fix")] string Fix,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("unrelated")] bool Unrelated,
    [property: JsonPropertyName("owning_repository")] string OwningRepository);

public sealed record ReviewResult([property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("findings")] IReadOnlyList<Finding> Findings);
