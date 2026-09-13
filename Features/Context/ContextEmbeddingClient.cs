using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using TerraScale.Reviewer.Features.Configuration;

namespace TerraScale.Reviewer.Features.Context;

internal sealed class ContextEmbeddingClient(HttpClient http, ReviewerOptions options)
{
    private readonly HttpClient _http = Configure(http, options);
    private readonly ReviewerOptions _options = options;

    private static HttpClient Configure(HttpClient client, ReviewerOptions options)
    {
        client.BaseAddress = new Uri(options.ProxyUrl + "/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ProxyToken);
        return client;
    }

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct)
    {
        var response = await _http.PostAsJsonAsync("embeddings", new { model = _options.EmbedModel, input = inputs }, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"embedding service status {(int)response.StatusCode}: {raw.Trim()}");
        using var document = JsonDocument.Parse(raw);
        var vectors = document.RootElement.GetProperty("data").EnumerateArray()
            .OrderBy(x => x.GetProperty("index").GetInt32())
            .Select(x => x.GetProperty("embedding").EnumerateArray().Select(v => v.GetSingle()).ToArray()).ToList();
        if (vectors.Count != inputs.Count) throw new InvalidOperationException("embedding service returned an unexpected number of vectors");
        return vectors;
    }
}
