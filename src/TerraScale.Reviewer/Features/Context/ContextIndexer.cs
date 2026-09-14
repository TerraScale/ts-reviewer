using System.Collections.Concurrent;
using System.Threading.Channels;
using TerraScale.Reviewer.Features.Configuration;
using TerraScale.Reviewer.Features.Forgejo;

namespace TerraScale.Reviewer.Features.Context;

public sealed class ContextIndexer : BackgroundService
{
    private readonly ForgejoClient _forgejo;
    private readonly ContextStore _store;
    private readonly ContextEmbedder _embeddings;
    private readonly ReviewerOptions _options;
    private readonly ILogger<ContextIndexer> _logger;
    private readonly Channel<IndexRequest> _queue = Channel.CreateUnbounded<IndexRequest>(new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pending = new(StringComparer.OrdinalIgnoreCase);

    internal ContextIndexer(ForgejoClient forgejo, ContextStore store, ContextEmbedder embeddings, ReviewerOptions options, ILogger<ContextIndexer> logger) =>
        (_forgejo, _store, _embeddings, _options, _logger) = (forgejo, store, embeddings, options, logger);

    public async Task EnsureIndexedAsync(string owner, string name, string commit, CancellationToken ct)
    {
        var repository = owner + "/" + name;
        if (_store.IsIndexed(repository, commit)) return;
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

    public Task<ContextSearchResponse> SearchAsync(ContextSearchRequest request, CancellationToken ct)
    {
        var repository = request.Repository.Trim();
        if (!_store.IsIndexed(repository, request.Commit)) return Task.FromResult(new ContextSearchResponse([], false));
        if (string.IsNullOrWhiteSpace(request.Query)) return Task.FromResult(new ContextSearchResponse([], true));
        var vector = _embeddings.Embed(request.Query);
        var results = _store.Search(repository, request.Commit, vector, Math.Clamp(request.TopK, 1, 50));
        return Task.FromResult(new ContextSearchResponse(results, true));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var request in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            var key = Key(request.Owner + "/" + request.Name, request.Commit);
            try
            {
                if (!_store.IsIndexed(request.Owner + "/" + request.Name, request.Commit))
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
        if (orderedChunks.Count > 0)
        {
            var inputs = orderedChunks.Select(x => $"Repository: {repository}\nPath: {x.Path}\nSymbol: {x.Symbol}\nLines: {x.StartLine}-{x.EndLine}\n{x.Content}").ToArray();
            var vectors = _embeddings.Embed(inputs);
            _store.ReplaceCommit(repository, request.Commit, orderedChunks.Zip(vectors, (chunk, vector) => (chunk, vector)).ToList());
        }
        else _store.ReplaceCommit(repository, request.Commit, []);
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
