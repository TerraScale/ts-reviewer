using System.Collections.Concurrent;
using System.Threading.Channels;
using TerraScale.Reviewer.Features.Configuration;
using TerraScale.Reviewer.Features.Forgejo;

namespace TerraScale.Reviewer.Features.Context;

public sealed class ContextIndexer : BackgroundService
{
    private readonly ForgejoClient _forgejo;
    private readonly ContextStore _store;
    private readonly ContextEmbeddingClient _embeddings;
    private readonly ReviewerOptions _options;
    private readonly ILogger<ContextIndexer> _logger;
    private readonly Channel<IndexRequest> _queue = Channel.CreateUnbounded<IndexRequest>(new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pending = new(StringComparer.OrdinalIgnoreCase);

    internal ContextIndexer(ForgejoClient forgejo, ContextStore store, ContextEmbeddingClient embeddings, ReviewerOptions options, ILogger<ContextIndexer> logger) =>
        (_forgejo, _store, _embeddings, _options, _logger) = (forgejo, store, embeddings, options, logger);

    public async Task EnsureIndexedAsync(string owner, string name, string commit, CancellationToken ct)
    {
        var repository = owner + "/" + name;
        if (await _store.IsIndexedAsync(repository, commit, ct)) return;
        var key = Key(repository, commit);
        var completion = _pending.GetOrAdd(key, _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        if (completion.Task.Status == TaskStatus.WaitingForActivation)
            await _queue.Writer.WriteAsync(new IndexRequest(owner, name, commit), ct);
        await completion.Task.WaitAsync(ct);
    }

    public bool Enqueue(string owner, string name, string commit)
    {
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(commit)) return false;
        var key = Key(owner + "/" + name, commit);
        _pending.GetOrAdd(key, _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        return _queue.Writer.TryWrite(new IndexRequest(owner, name, commit));
    }

    public async Task<ContextSearchResponse> SearchAsync(ContextSearchRequest request, CancellationToken ct)
    {
        var repository = request.Repository.Trim();
        var indexed = await _store.IsIndexedAsync(repository, request.Commit, ct);
        var vector = indexed && !string.IsNullOrWhiteSpace(request.Query) ? (await _embeddings.EmbedAsync([request.Query], ct))[0] : null;
        var results = indexed ? await _store.SearchAsync(repository, request.Commit, request.Query, Math.Clamp(request.TopK, 1, 50), vector, ct) : [];
        return new ContextSearchResponse(results, indexed);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var request in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            var key = Key(request.Owner + "/" + request.Name, request.Commit);
            try
            {
                if (!await _store.IsIndexedAsync(request.Owner + "/" + request.Name, request.Commit, stoppingToken))
                    await IndexAsync(request, stoppingToken);
                Complete(key, true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "context indexing failed for {Repository}@{Commit}", request.Owner + "/" + request.Name, request.Commit);
                Complete(key, false, ex);
            }
        }
    }

    private async Task IndexAsync(IndexRequest request, CancellationToken ct)
    {
        var repository = request.Owner + "/" + request.Name;
        var tree = await _forgejo.TreeAsync(request.Owner, request.Name, request.Commit, ct);
        var files = tree.Where(x => x.Type == "blob" && x.Size is > 0 and <= 1_000_000 && IsSourceFile(x.Path)).Take(_options.ContextMaxFiles).ToList();
        var chunks = new ConcurrentBag<CodeChunk>();
        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(_options.MaxConcurrent, 16), CancellationToken = ct }, async (file, token) =>
        {
            var content = await _forgejo.FileAsync(request.Owner, request.Name, request.Commit, file.Path, token);
            foreach (var chunk in CodeChunker.Split(file.Path, content)) chunks.Add(chunk);
        });
        var orderedChunks = chunks.OrderBy(x => x.Path, StringComparer.Ordinal).ThenBy(x => x.StartLine).ToList();
        if (orderedChunks.Count == 0)
        {
            await _store.ReplaceCommitAsync(repository, request.Commit, [], ct);
            return;
        }
        var all = new List<(CodeChunk Chunk, float[] Vector)>();
        foreach (var batch in orderedChunks.Chunk(64))
        {
            var inputs = batch.Select(x => $"Repository: {repository}\nPath: {x.Path}\nSymbol: {x.Symbol}\nLines: {x.StartLine}-{x.EndLine}\n{x.Content}").ToArray();
            var vectors = await _embeddings.EmbedAsync(inputs, ct);
            all.AddRange(batch.Zip(vectors, (chunk, vector) => (chunk, vector)));
        }
        await _store.ReplaceCommitAsync(repository, request.Commit, all, ct);
        _logger.LogInformation("indexed {Repository}@{Commit}: {Files} files, {Chunks} chunks", repository, request.Commit, files.Count, orderedChunks.Count);
    }

    private void Complete(string key, bool result, Exception? error = null)
    {
        if (_pending.TryRemove(key, out var completion))
        {
            if (error is null) completion.TrySetResult(result);
            else completion.TrySetException(error);
        }
    }

    private static string Key(string repository, string commit) => repository + "@" + commit;
    private static bool IsSourceFile(string path) => new[] { ".cs", ".go", ".rs", ".ts", ".tsx", ".js", ".jsx", ".py", ".java", ".kt", ".sql", ".json", ".yaml", ".yml", ".toml", ".md", ".proto", ".csproj", ".sln" }.Any(x => path.EndsWith(x, StringComparison.OrdinalIgnoreCase));
}
