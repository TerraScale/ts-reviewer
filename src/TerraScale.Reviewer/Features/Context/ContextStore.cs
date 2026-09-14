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

internal sealed class ContextStore : IDisposable
{
    private readonly LiteDatabase _database;
    private readonly ILiteCollection<ChunkDocument> _chunks;
    private readonly ILiteCollection<CommitDocument> _commits;
    private readonly ILiteCollection<BsonDocument> _meta;
    private int _vectorDimensions;

    public ContextStore(ReviewerOptions options)
    {
        var path = Path.GetFullPath(options.ContextDatabasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _database = new LiteDatabase(path);
        _chunks = _database.GetCollection<ChunkDocument>("chunks");
        _chunks.EnsureIndex(x => x.Repository);
        _chunks.EnsureIndex(x => x.CommitSha);
        _commits = _database.GetCollection<CommitDocument>("commits");
        _meta = _database.GetCollection("meta");
    }

    public bool IsIndexed(string repository, string commit) =>
        _commits.Exists(x => x.Id == CommitKey(repository, commit));

    public void ReplaceCommit(string repository, string commit, IReadOnlyList<(CodeChunk Chunk, float[] Vector)> chunks)
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

    public IReadOnlyList<ContextHit> Search(string repository, string commit, float[] vector, int topK)
    {
        EnsureVectorIndex(vector.Length);
        // ponytail: residual Where filters force an exact scan over all vectors instead of HNSW ANN;
        // per-commit isolation matters more than recall here, revisit if the chunk count grows past ~1M
        return _chunks.Query()
            .Where(x => x.Repository == repository && x.CommitSha == commit)
            .TopKNearWithScore(x => x.Embedding, vector, topK)
            .Select(hit => new ContextHit(repository, commit, hit.Document.Path, hit.Document.StartLine,
                hit.Document.EndLine, hit.Document.Symbol, hit.Document.Content, 1d - (hit.Score ?? 1d)))
            .ToList();
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
}
