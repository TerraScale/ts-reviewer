using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using FastBertTokenizer;
using Microsoft.ML.OnnxRuntime;

namespace TerraScale.Reviewer.Features.Context;

/// <summary>
/// Local text embedding with the bge-micro-v2 ONNX model and vocabulary that the
/// SmartComponents.LocalEmbeddings package downloads at build time, so it runs
/// fully offline. The session is built here rather than through the package's
/// LocalEmbedder, whose default session pins one onnxruntime thread per host
/// core and logs pthread_setaffinity_np errors inside a CPU-limited container.
/// Each inference runs on one thread and a batch spreads its inputs across
/// threads instead, which suits a model this small. Vectors match
/// LocalEmbedder's: the sum of the model's token vectors.
/// </summary>
internal sealed class ContextEmbedder : IDisposable
{
    private const int MaximumTokens = 512;
    private static readonly string ModelDirectory = Path.Combine(AppContext.BaseDirectory, "LocalEmbeddingsModel", "default");

    private readonly InferenceSession _session;
    private readonly int _dimensions;
    private readonly int _parallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
    // The tokenizer keeps per-call state, so each concurrent inference rents its own.
    private readonly ConcurrentBag<BertTokenizer> _tokenizers = [];

    public ContextEmbedder()
    {
        using var options = new Microsoft.ML.OnnxRuntime.SessionOptions { IntraOpNumThreads = 1, InterOpNumThreads = 1 };
        _session = new InferenceSession(Path.Combine(ModelDirectory, "model.onnx"), options);
        _dimensions = _session.OutputMetadata.First().Value.Dimensions[^1];
    }

    public float[] Embed(string input)
    {
        var tokenizer = _tokenizers.TryTake(out var pooled) ? pooled : LoadTokenizer();
        var (ids, mask, types) = tokenizer.Encode(input, MaximumTokens);
        var values = _session.InputNames.Select(name => Tensor(name switch
        {
            "input_ids" => ids,
            "attention_mask" => mask,
            "token_type_ids" => types,
            _ => throw new InvalidOperationException("unknown embedding model input " + name),
        })).ToList();
        try
        {
            using var run = new RunOptions();
            using var outputs = _session.Run(run, _session.InputNames, values, _session.OutputNames);
            var tokens = outputs[0].GetTensorDataAsSpan<float>();
            var vector = new float[_dimensions];
            for (var offset = 0; offset < tokens.Length; offset += _dimensions)
                for (var i = 0; i < _dimensions; i++) vector[i] += tokens[offset + i];
            return vector;
        }
        finally
        {
            foreach (var value in values) value.Dispose();
            _tokenizers.Add(tokenizer);
        }
    }

    public IReadOnlyList<float[]> Embed(IReadOnlyList<string> inputs)
    {
        var vectors = new float[inputs.Count][];
        Parallel.For(0, inputs.Count, new ParallelOptions { MaxDegreeOfParallelism = _parallelism }, i => vectors[i] = Embed(inputs[i]));
        return vectors;
    }

    public void Dispose() => _session.Dispose();

    private static OrtValue Tensor(ReadOnlyMemory<long> values) =>
        OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, MemoryMarshal.AsMemory(values), [1, values.Length]);

    private static BertTokenizer LoadTokenizer()
    {
        var tokenizer = new BertTokenizer();
        using var vocabulary = new StreamReader(Path.Combine(ModelDirectory, "vocab.txt"));
        tokenizer.LoadVocabulary(vocabulary, convertInputToLowercase: true, "[UNK]", "[CLS]", "[SEP]", "[PAD]", NormalizationForm.FormD);
        return tokenizer;
    }
}
