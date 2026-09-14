using SmartComponents.LocalEmbeddings;

namespace TerraScale.Reviewer.Features.Context;

/// <summary>
/// Local text embedding (ONNX bge-micro-v2, runs fully offline once the model is
/// downloaded at build time by the SmartComponents.LocalEmbeddings package).
/// </summary>
internal sealed class ContextEmbedder : IDisposable
{
    private readonly LocalEmbedder _embedder = new();

    public float[] Embed(string input) => _embedder.Embed(input).Values.ToArray();

    public IReadOnlyList<float[]> Embed(IReadOnlyList<string> inputs) =>
        _embedder.EmbedRange(inputs).Select(x => x.Embedding.Values.ToArray()).ToList();

    public void Dispose() => _embedder.Dispose();
}
