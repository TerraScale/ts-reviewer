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
    private readonly string _workerId = Environment.MachineName + ":" + Environment.ProcessId + ":" + Guid.NewGuid().ToString("N");
    private const int QueueCapacity = 1024;
    private const int FetchParallelism = 2; // Bound simultaneous Forgejo file downloads independently of review workers.
    private readonly Channel<IndexRequest> _queue;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pending = new(StringComparer.OrdinalIgnoreCase);

    internal ContextIndexer(ForgejoClient forgejo, ContextStore store, ContextEmbedder embeddings, ReviewerOptions options, ILogger<ContextIndexer> logger)
    {
        (_forgejo, _store, _embeddings, _options, _logger) = (forgejo, store, embeddings, options, logger);
        _queue = Channel.CreateBounded<IndexRequest>(new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
    }

    public async Task EnsureIndexedAsync(string owner, string name, string commit, CancellationToken ct)
    {
        var repository = owner + "/" + name;
        var key = Key(repository, commit);
        if (_pending.TryGetValue(key, out var existingPending))
        {
            await existingPending.Task.WaitAsync(ct);
            return;
        }
        if (_store.IsIndexed(repository, commit)) return;
        var (completion, created) = GetOrCreatePending(key);
        if (created)
        {
            _store.EnqueueIndexJob(owner, name, commit);
            await _queue.Writer.WriteAsync(new IndexRequest(owner, name, commit), ct);
        }
        await completion.Task.WaitAsync(ct);
    }

    public bool Enqueue(string owner, string name, string commit)
    {
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(commit)) return false;
        var repository = owner + "/" + name;
        var key = Key(repository, commit);
        // A pending in-process request is already the durable admission record;
        // avoid contending with the worker's LiteDB transaction for duplicates.
        if (_pending.ContainsKey(key)) return true;
        if (_store.IsIndexed(repository, commit)) return true;
        var (_, created) = GetOrCreatePending(key);
        if (!created) return true;
        _store.EnqueueIndexJob(owner, name, commit);
        if (_queue.Writer.TryWrite(new IndexRequest(owner, name, commit))) return true;
        if (_pending.TryRemove(key, out var completion))
            completion.TrySetException(new InvalidOperationException("context indexing queue is full"));
        return false;
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

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Writer.TryComplete();
        await base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            foreach (var job in _store.PendingIndexJobs())
            {
                var key = Key(job.Owner + "/" + job.Name, job.Commit);
                var (_, created) = GetOrCreatePending(key);
                if (created)
                    await _queue.Writer.WriteAsync(new IndexRequest(job.Owner, job.Name, job.Commit), stoppingToken);
            }

            await foreach (var request in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                var key = Key(request.Owner + "/" + request.Name, request.Commit);
                var repository = request.Owner + "/" + request.Name;
                var lease = _store.TryClaimIndexJob(repository, request.Commit, _workerId, TimeSpan.FromMinutes(5));
                if (lease is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                    await _queue.Writer.WriteAsync(request, stoppingToken);
                    continue;
                }
                var completed = false;
                try
                {
                    var status = _store.GetIndexJobStatus(repository, request.Commit);
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (status is { NextAttemptAt: > 0 } && status.NextAttemptAt > now)
                        await Task.Delay(TimeSpan.FromMilliseconds(status.NextAttemptAt - now), stoppingToken);
                    if (!_store.IsIndexed(repository, request.Commit))
                    {
                        await IndexAsync(request, stoppingToken);
                    }
                    _store.RemoveIndexJob(repository, request.Commit);
                    completed = true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "context indexing failed for {Repository}@{Commit}", request.Owner + "/" + request.Name, request.Commit);
                    var failure = _store.RecordIndexFailure(
                        repository,
                        request.Commit,
                        ex.Message,
                        _options.ContextMaxAttempts,
                        _options.ContextRetryDelay);
                    if (failure.DeadLettered)
                    {
                        Complete(key, false, new InvalidOperationException(
                            $"context indexing moved to dead-letter after {_options.ContextMaxAttempts} attempts", ex));
                    }
                    else
                    {
                        _logger.LogWarning("retrying context indexing for {Repository}@{Commit} in {Delay}",
                            request.Owner + "/" + request.Name, request.Commit, failure.Delay);
                        await Task.Delay(failure.Delay, stoppingToken);
                        await _queue.Writer.WriteAsync(request, stoppingToken);
                    }
                }
                finally
                {
                    _store.ReleaseIndexJob(repository, request.Commit, _workerId, lease);
                    if (completed) Complete(key, true);
                }
            }
        }
        finally
        {
            var stopped = new OperationCanceledException("context indexing stopped", stoppingToken);
            foreach (var pair in _pending)
                if (_pending.TryRemove(pair.Key, out var completion))
                    completion.TrySetException(stopped);
        }
    }

    private async Task IndexAsync(IndexRequest request, CancellationToken ct)
    {
        var repository = request.Owner + "/" + request.Name;
        var tree = await _forgejo.TreeAsync(request.Owner, request.Name, request.Commit, ct);
        var files = tree.Where(x => x.Type == "blob" && x.Size is > 0 and <= 1_000_000 && IsSourceFile(x.Path)).Take(_options.ContextMaxFiles).ToList();
        var chunks = new ConcurrentBag<CodeChunk>();
        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = FetchParallelism, CancellationToken = ct }, async (file, token) =>
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

    private (TaskCompletionSource<bool> Completion, bool Created) GetOrCreatePending(string key)
    {
        while (true)
        {
            if (_pending.TryGetValue(key, out var existing)) return (existing, false);
            var candidate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_pending.TryAdd(key, candidate)) return (candidate, true);
        }
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
