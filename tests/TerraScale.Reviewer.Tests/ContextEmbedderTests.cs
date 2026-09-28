using SmartComponents.LocalEmbeddings;
using TerraScale.Reviewer.Features.Context;
using TUnit.Assertions;
using TUnit.Core;

public sealed class ContextEmbedderTests
{
    [Test]
    public async Task VectorsMatchTheLocalEmbeddingsPackage()
    {
        string[] inputs =
        [
            "CreateWidget",
            "Repository: TerraScale/widgets\nPath: src/Widgets.cs\nSymbol: Pricing\nLines: 1-9\npublic static decimal Total(decimal price, int quantity) => price * quantity;",
            "Unicode and accents: café, naïve, 数据库, emoji 🚀",
            string.Join(' ', Enumerable.Repeat("token overflow beyond the model limit", 200)),
        ];
        using var reference = new LocalEmbedder();
        using var embedder = new ContextEmbedder();

        var batch = embedder.Embed(inputs);
        for (var i = 0; i < inputs.Length; i++)
        {
            var expected = reference.Embed(inputs[i]).Values.ToArray();
            var actual = embedder.Embed(inputs[i]);
            await Assert.That(actual.Length).IsEqualTo(expected.Length);
            await Assert.That(batch[i]).IsEquivalentTo(actual);
            var worst = expected.Zip(actual, (x, y) => Math.Abs(x - y) / Math.Max(1f, Math.Abs(x))).Max();
            await Assert.That(worst).IsLessThan(1e-4f);
        }
    }
}
