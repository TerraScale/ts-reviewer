using LiteDB;
using LiteDB.Vector;
using TerraScale.Reviewer.Features.Configuration;

namespace TerraScale.Reviewer.Features.Context;

internal sealed class ChunkDocument
{
    public ObjectId Id { get; set; } = ObjectId.NewObjectId();
    public string Repository { get; set; } = string.Empty;
    public string CommitSha { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public float[] Embedding { get; set; } = [];
}

internal sealed class CommitDocument
{
    public string Id { get; set; } = string.Empty;
    public string Repository { get; set; } = string.Empty;
    public string CommitSha { get; set; } = string.Empty;
    public int ChunkCount { get; set; }
}

internal sealed class IndexJobDocument
{
    public string Id { get; set; } = string.Empty;
    public string Repository { get; set; } = string.Empty;
    public string Owner { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string CommitSha { get; set; } = string.Empty;
    public long EnqueuedAt { get; set; }
    public int Attempts { get; set; }
    /// <summary>Unix epoch milliseconds; zero means immediately eligible.</summary>
    public long NextAttemptAt { get; set; }
    public string? LastError { get; set; }
    public bool DeadLettered { get; set; }
    public long DeadAt { get; set; }
    public string LeaseOwner { get; set; } = string.Empty;
    public string LeaseToken { get; set; } = string.Empty;
    public long LeaseUntil { get; set; }
}

internal sealed class ContextStore : IDisposable
{
    private readonly object _gate = new();
    private readonly LiteDatabase _database;
    private readonly ILiteCollection<ChunkDocument> _chunks;
    private readonly ILiteCollection<CommitDocument> _commits;
    private readonly ILiteCollection<BsonDocument> _meta;
    private readonly ILiteCollection<IndexJobDocument> _jobs;
    private readonly ILiteCollection<BsonDocument> _deliveries;
    private int _vectorDimensions;

    public ContextStore(ReviewerOptions options)
    {
        var path = Path.GetFullPath(options.ContextDatabasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Shared mode coordinates the file header and transactions between
        // independently hosted reviewer processes. Direct mode permits only
        // one process and can fail during a rolling deployment or recovery.
        // The collation is pinned to the invariant binary collation (LCID 127)
        // because the service runs with InvariantGlobalization: a header written
        // with a culture LCID (for example en-US) crashes invariant-mode workers
        // with CultureNotFoundException when they open the same file.
        _database = new LiteDatabase(new ConnectionString($"Filename={path};Connection=Shared;Collation=/Ordinal"));
        _chunks = _database.GetCollection<ChunkDocument>("chunks");
        _chunks.EnsureIndex(x => x.Repository);
        _chunks.EnsureIndex(x => x.CommitSha);
        _commits = _database.GetCollection<CommitDocument>("commits");
        _meta = _database.GetCollection("meta");
        _vectorDimensions = _meta.FindById("embedding_dimensions")?["dimensions"].AsInt32 ?? 0;
        _jobs = _database.GetCollection<IndexJobDocument>("index_jobs");
        _jobs.EnsureIndex(x => x.Repository);
        _deliveries = _database.GetCollection("webhook_deliveries");
    }

    public string? TryBeginWebhookDelivery(string id)
    {
        lock (_gate)
        {
            _database.BeginTrans();
            try
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var delivery = _deliveries.FindById(id);
                if (delivery is not null && (delivery["complete"].AsBoolean || delivery["until"].AsInt64 > now))
                {
                    _database.Rollback();
                    return null;
                }
                var token = Guid.NewGuid().ToString("N");
                _deliveries.Upsert(new BsonDocument { ["_id"] = id, ["token"] = token,
                    ["complete"] = false, ["until"] = now + 300_000 });
                _database.Commit();
                return token;
            }
            catch { _database.Rollback(); throw; }
        }
    }

    public void CompleteWebhookDelivery(string id, string token) => FinishWebhookDelivery(id, token, true);
    public void ReleaseWebhookDelivery(string id, string token) => FinishWebhookDelivery(id, token, false);

    private void FinishWebhookDelivery(string id, string token, bool complete)
    {
        lock (_gate)
        {
            _database.BeginTrans();
            try
            {
                var delivery = _deliveries.FindById(id);
                if (delivery is not null && delivery["token"].AsString == token)
                {
                    if (complete)
                    {
                        delivery["complete"] = true;
                        delivery["token"] = "";
                        _deliveries.Update(delivery);
                    }
                    else _deliveries.Delete(id);
                }
                _database.Commit();
            }
            catch { _database.Rollback(); throw; }
        }
    }

    public bool IsIndexed(string repository, string commit)
    {
        lock (_gate) return _commits.Exists(x => x.Id == CommitKey(repository, commit));
    }

    public void EnqueueIndexJob(string owner, string name, string commit)
    {
        lock (_gate)
        {
            var repository = owner + "/" + name;
            var id = CommitKey(repository, commit);
            var existing = _jobs.FindById(id);
            var reset = existing is null || existing.DeadLettered;
            _jobs.Upsert(new IndexJobDocument
            {
                Id = id,
                Repository = repository,
                Owner = owner,
                Name = name,
                CommitSha = commit,
                EnqueuedAt = existing?.EnqueuedAt ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Attempts = reset ? 0 : existing!.Attempts,
                NextAttemptAt = reset ? 0 : existing!.NextAttemptAt,
                LastError = reset ? null : existing!.LastError,
                DeadLettered = false,
                DeadAt = 0,
                LeaseOwner = string.Empty,
                LeaseToken = string.Empty,
                LeaseUntil = 0,
            });
        }
    }

    /// <summary>Claims one job with an expiring fencing lease. LiteDB's transaction
    /// serializes competing processes; the token prevents an expired worker from
    /// releasing a lease that a replacement worker already owns.</summary>
    public string? TryClaimIndexJob(string repository, string commit, string owner, TimeSpan lifetime)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _database.BeginTrans();
            try
            {
                var job = _jobs.FindById(CommitKey(repository, commit));
                if (job is null || job.DeadLettered || (job.LeaseUntil > now && !string.Equals(job.LeaseOwner, owner, StringComparison.Ordinal)))
                {
                    _database.Rollback();
                    return null;
                }

                var token = Guid.NewGuid().ToString("N");
                job.LeaseOwner = owner;
                job.LeaseToken = token;
                job.LeaseUntil = now + Math.Max(1, (long)lifetime.TotalMilliseconds);
                _jobs.Update(job);
                _database.Commit();
                return token;
            }
            catch
            {
                _database.Rollback();
                throw;
            }
        }
    }

    public void ReleaseIndexJob(string repository, string commit, string owner, string token)
    {
        lock (_gate)
        {
            _database.BeginTrans();
            try
            {
                var job = _jobs.FindById(CommitKey(repository, commit));
                if (job is not null && job.LeaseOwner == owner && job.LeaseToken == token)
                {
                    job.LeaseOwner = string.Empty;
                    job.LeaseToken = string.Empty;
                    job.LeaseUntil = 0;
                    _jobs.Update(job);
                }
                _database.Commit();
            }
            catch
            {
                _database.Rollback();
                throw;
            }
        }
    }

    public IReadOnlyList<IndexJob> PendingIndexJobs()
    {
        lock (_gate) return _jobs.FindAll()
            .Where(job => !job.DeadLettered)
            .OrderBy(job => job.EnqueuedAt)
            .Select(job => new IndexJob(job.Owner, job.Name, job.CommitSha))
            .ToList();
    }

    public (bool DeadLettered, TimeSpan Delay) RecordIndexFailure(string repository, string commit,
        string error, int maxAttempts, TimeSpan baseDelay)
    {
        lock (_gate)
        {
            var id = CommitKey(repository, commit);
            var job = _jobs.FindById(id);
            if (job is null)
                return (true, TimeSpan.Zero);

            var attempts = checked(job.Attempts + 1);
            var deadLettered = attempts >= maxAttempts;
            var delaySeconds = Math.Min(30d, Math.Pow(2, Math.Max(0, attempts - 1)) * Math.Max(0.1, baseDelay.TotalSeconds));
            job.Attempts = attempts;
            job.LastError = error.Length > 2_000 ? error[..2_000] : error;
            job.DeadLettered = deadLettered;
            job.NextAttemptAt = deadLettered
                ? 0
                : DateTimeOffset.UtcNow.AddSeconds(delaySeconds).ToUnixTimeMilliseconds();
            job.DeadAt = deadLettered ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : 0;
            job.LeaseOwner = string.Empty;
            job.LeaseToken = string.Empty;
            job.LeaseUntil = 0;
            _jobs.Update(job);
            return (deadLettered, TimeSpan.FromSeconds(delaySeconds));
        }
    }

    public IndexJobStatus? GetIndexJobStatus(string repository, string commit)
    {
        lock (_gate)
        {
            var job = _jobs.FindById(CommitKey(repository, commit));
            return job is null ? null : new IndexJobStatus(job.Attempts, job.NextAttemptAt, job.LastError, job.DeadLettered);
        }
    }

    public IReadOnlyList<IndexDeadLetter> DeadLetterJobs(int limit = 100)
    {
        lock (_gate) return _jobs.Find(x => x.DeadLettered)
            .OrderByDescending(x => x.EnqueuedAt)
            .Take(Math.Clamp(limit, 1, 100))
            .Select(x => new IndexDeadLetter(x.Repository, x.CommitSha, x.Attempts, x.EnqueuedAt, x.LastError, x.DeadAt))
            .ToList();
    }

    public bool ReplayDeadLetter(string repository, string commit)
    {
        lock (_gate)
        {
            var job = _jobs.FindById(CommitKey(repository, commit));
            if (job is null || !job.DeadLettered) return false;
            job.Attempts = 0;
            job.NextAttemptAt = 0;
            job.LastError = null;
            job.DeadLettered = false;
            job.DeadAt = 0;
            job.LeaseOwner = string.Empty;
            job.LeaseToken = string.Empty;
            job.LeaseUntil = 0;
            _jobs.Update(job);
            return true;
        }
    }

    public void RemoveIndexJob(string repository, string commit)
    {
        lock (_gate) _jobs.Delete(CommitKey(repository, commit));
    }

    public void ReplaceCommit(string repository, string commit, IReadOnlyList<(CodeChunk Chunk, float[] Vector)> chunks)
    {
        lock (_gate)
        {
            _database.BeginTrans();
            try
            {
                _chunks.DeleteMany(x => x.Repository == repository && x.CommitSha == commit);
                if (chunks.Count > 0) EnsureVectorIndex(chunks[0].Vector.Length);
                _chunks.InsertBulk(chunks.Select(item => new ChunkDocument
                {
                    Repository = repository,
                    CommitSha = commit,
                    Path = item.Chunk.Path,
                    StartLine = item.Chunk.StartLine,
                    EndLine = item.Chunk.EndLine,
                    Symbol = item.Chunk.Symbol,
                    Content = item.Chunk.Content,
                    Embedding = item.Vector,
                }));
                _commits.Upsert(new CommitDocument { Id = CommitKey(repository, commit), Repository = repository, CommitSha = commit, ChunkCount = chunks.Count });
                _database.Commit();
            }
            catch
            {
                _database.Rollback();
                throw;
            }
        }
    }

    public IReadOnlyList<ContextHit> Search(string repository, string commit, float[] vector, int topK)
    {
        lock (_gate)
        {
            if (_vectorDimensions is not 0 && _vectorDimensions != vector.Length)
                throw new InvalidOperationException($"context database uses {_vectorDimensions}-dimension vectors but the local embedding model produces {vector.Length}; delete the database to reindex");
            if (_vectorDimensions == 0) return [];
            var queryNorm = Math.Sqrt(vector.Sum(value => (double)value * value));
            if (queryNorm == 0) return [];
            return _chunks.Find(x => x.Repository == repository && x.CommitSha == commit)
                .Select(document => (document, score: Cosine(document.Embedding, vector, queryNorm)))
                .OrderByDescending(item => item.score)
                .Take(Math.Clamp(topK, 1, 50))
                .Select(item => new ContextHit(repository, commit, item.document.Path, item.document.StartLine,
                    item.document.EndLine, item.document.Symbol, item.document.Content, item.score))
                .ToList();
        }
    }

    public void Dispose() => _database.Dispose();

    private void EnsureVectorIndex(int dimensions)
    {
        if (_vectorDimensions == dimensions) return;
        var stored = _meta.FindById("embedding_dimensions")?["dimensions"].AsInt32 ?? 0;
        if (stored is not 0 && stored != dimensions)
            throw new InvalidOperationException($"context database uses {stored}-dimension vectors but the local embedding model produces {dimensions}; delete the database to reindex");
        if (stored == 0)
            _meta.Insert(new BsonDocument { ["_id"] = "embedding_dimensions", ["dimensions"] = dimensions });
        _chunks.EnsureIndex(x => x.Embedding, new VectorIndexOptions((ushort)dimensions, VectorDistanceMetric.Cosine));
        _vectorDimensions = dimensions;
    }

    private static string CommitKey(string repository, string commit) => repository + "@" + commit;

    private static double Cosine(float[] left, float[] right, double rightNorm)
    {
        var dot = 0d;
        var leftNorm = 0d;
        for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
        {
            dot += left[index] * right[index];
            leftNorm += left[index] * left[index];
        }
        return leftNorm == 0 ? 0 : dot / (Math.Sqrt(leftNorm) * rightNorm);
    }
}
