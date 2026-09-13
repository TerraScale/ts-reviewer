using Microsoft.Data.Sqlite;
using TerraScale.Reviewer.Features.Configuration;

namespace TerraScale.Reviewer.Features.Context;

internal sealed class ContextStore
{
    private readonly ReviewerOptions _options;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ContextStore(ReviewerOptions options)
    {
        _options = options;
        var path = Path.GetFullPath(options.ContextDatabasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared }.ToString();
        using var connection = Open();
        Initialize(connection);
    }

    public async Task<bool> IsIndexedAsync(string repository, string commit, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM commits WHERE repository = $repository AND commit_sha = $commit AND status = 'ready')";
            command.Parameters.AddWithValue("$repository", repository);
            command.Parameters.AddWithValue("$commit", commit);
            return Convert.ToInt32(await command.ExecuteScalarAsync(ct)) == 1;
        }
        finally { _gate.Release(); }
    }

    public async Task ReplaceCommitAsync(string repository, string commit, IReadOnlyList<(CodeChunk Chunk, float[] Vector)> chunks, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            var ids = new List<long>();
            var existingIds = new List<long>();
            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "SELECT id FROM chunks WHERE repository = $repository AND commit_sha = $commit";
                delete.Parameters.AddWithValue("$repository", repository);
                delete.Parameters.AddWithValue("$commit", commit);
                using var reader = delete.ExecuteReader();
                while (reader.Read()) existingIds.Add(reader.GetInt64(0));
            }
            foreach (var existingId in existingIds)
            {
                using var deleteVector = connection.CreateCommand();
                deleteVector.Transaction = transaction;
                deleteVector.CommandText = "DELETE FROM chunk_vectors WHERE chunk_id = $id";
                deleteVector.Parameters.AddWithValue("$id", existingId);
                deleteVector.ExecuteNonQuery();
                using var deleteFts = connection.CreateCommand();
                deleteFts.Transaction = transaction;
                deleteFts.CommandText = "DELETE FROM chunks_fts WHERE chunk_id = $id";
                deleteFts.Parameters.AddWithValue("$id", existingId);
                deleteFts.ExecuteNonQuery();
            }
            using (var deleteChunks = connection.CreateCommand())
            {
                deleteChunks.Transaction = transaction;
                deleteChunks.CommandText = "DELETE FROM chunks WHERE repository = $repository AND commit_sha = $commit";
                deleteChunks.Parameters.AddWithValue("$repository", repository);
                deleteChunks.Parameters.AddWithValue("$commit", commit);
                deleteChunks.ExecuteNonQuery();
            }
            using (var deleteCommit = connection.CreateCommand())
            {
                deleteCommit.Transaction = transaction;
                deleteCommit.CommandText = "DELETE FROM commits WHERE repository = $repository AND commit_sha = $commit";
                deleteCommit.Parameters.AddWithValue("$repository", repository);
                deleteCommit.Parameters.AddWithValue("$commit", commit);
                deleteCommit.ExecuteNonQuery();
            }
            foreach (var item in chunks)
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO chunks(repository, commit_sha, path, start_line, end_line, symbol, content) VALUES ($repository, $commit_sha, $path, $start, $end, $symbol, $content) RETURNING id";
                insert.Parameters.AddWithValue("$repository", repository);
                insert.Parameters.AddWithValue("$commit_sha", commit);
                insert.Parameters.AddWithValue("$path", item.Chunk.Path);
                insert.Parameters.AddWithValue("$start", item.Chunk.StartLine);
                insert.Parameters.AddWithValue("$end", item.Chunk.EndLine);
                insert.Parameters.AddWithValue("$symbol", item.Chunk.Symbol);
                insert.Parameters.AddWithValue("$content", item.Chunk.Content);
                var id = Convert.ToInt64(insert.ExecuteScalar());
                ids.Add(id);
                using var fts = connection.CreateCommand();
                fts.Transaction = transaction;
                fts.CommandText = "INSERT INTO chunks_fts(chunk_id, content, path, symbol) VALUES ($id, $content, $path, $symbol)";
                fts.Parameters.AddWithValue("$id", id);
                fts.Parameters.AddWithValue("$content", item.Chunk.Content);
                fts.Parameters.AddWithValue("$path", item.Chunk.Path);
                fts.Parameters.AddWithValue("$symbol", item.Chunk.Symbol);
                fts.ExecuteNonQuery();
                using var vector = connection.CreateCommand();
                vector.Transaction = transaction;
                vector.CommandText = "INSERT INTO chunk_vectors(chunk_id, embedding) VALUES ($id, $embedding)";
                vector.Parameters.AddWithValue("$id", id);
                vector.Parameters.Add("$embedding", SqliteType.Blob).Value = Serialize(item.Vector);
                vector.ExecuteNonQuery();
            }
            using var commitCommand = connection.CreateCommand();
            commitCommand.Transaction = transaction;
            commitCommand.CommandText = "INSERT INTO commits(repository, commit_sha, indexed_at, status, chunk_count) VALUES ($repository, $commit_sha, $indexed_at, 'ready', $count)";
            commitCommand.Parameters.AddWithValue("$repository", repository);
            commitCommand.Parameters.AddWithValue("$commit_sha", commit);
            commitCommand.Parameters.AddWithValue("$indexed_at", DateTimeOffset.UtcNow.ToString("O"));
            commitCommand.Parameters.AddWithValue("$count", ids.Count);
            commitCommand.ExecuteNonQuery();
            transaction.Commit();
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<ContextHit>> SearchAsync(string repository, string commit, string query, int topK, IReadOnlyList<float>? vector, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var connection = Open();
            var hits = new Dictionary<long, ContextHit>();
            if (vector is not null)
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT c.id, c.path, c.start_line, c.end_line, c.symbol, c.content, v.distance FROM chunk_vectors v JOIN chunks c ON c.id = v.chunk_id WHERE v.embedding MATCH $embedding AND k = $k AND c.repository = $repository AND c.commit_sha = $commit ORDER BY v.distance";
                command.Parameters.AddWithValue("$embedding", Serialize(vector.ToArray()));
                command.Parameters.AddWithValue("$k", topK);
                command.Parameters.AddWithValue("$repository", repository);
                command.Parameters.AddWithValue("$commit", commit);
                using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) hits[reader.GetInt64(0)] = ReadHit(reader, repository, commit, 1d - reader.GetDouble(6));
            }
            if (!string.IsNullOrWhiteSpace(query))
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT c.id, c.path, c.start_line, c.end_line, c.symbol, c.content, bm25(chunks_fts) FROM chunks_fts JOIN chunks c ON c.id = chunks_fts.chunk_id WHERE chunks_fts MATCH $query AND c.repository = $repository AND c.commit_sha = $commit ORDER BY bm25(chunks_fts) LIMIT $k";
                command.Parameters.AddWithValue("$query", SanitizeFts(query));
                command.Parameters.AddWithValue("$repository", repository);
                command.Parameters.AddWithValue("$commit", commit);
                command.Parameters.AddWithValue("$k", topK);
                using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var id = reader.GetInt64(0);
                    var hit = ReadHit(reader, repository, commit, 0.5d);
                    hits[id] = hits.TryGetValue(id, out var existing) ? existing with { Score = existing.Score + hit.Score } : hit;
                }
            }
            return hits.Values.OrderByDescending(x => x.Score).Take(topK).ToList();
        }
        finally { _gate.Release(); }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        connection.LoadVector();
        return connection;
    }

    private void Initialize(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA journal_mode = WAL; CREATE TABLE IF NOT EXISTS commits(repository TEXT NOT NULL, commit_sha TEXT NOT NULL, indexed_at TEXT NOT NULL, status TEXT NOT NULL, chunk_count INTEGER NOT NULL, PRIMARY KEY(repository, commit_sha)); CREATE TABLE IF NOT EXISTS chunks(id INTEGER PRIMARY KEY, repository TEXT NOT NULL, commit_sha TEXT NOT NULL, path TEXT NOT NULL, start_line INTEGER NOT NULL, end_line INTEGER NOT NULL, symbol TEXT NOT NULL, content TEXT NOT NULL); CREATE VIRTUAL TABLE IF NOT EXISTS chunks_fts USING fts5(chunk_id UNINDEXED, content, path, symbol); CREATE VIRTUAL TABLE IF NOT EXISTS chunk_vectors USING vec0(chunk_id INTEGER PRIMARY KEY, embedding FLOAT[{_options.ContextEmbeddingDimensions}] distance_metric=cosine);";
        command.ExecuteNonQuery();
    }

    private static ContextHit ReadHit(SqliteDataReader reader, string repository, string commit, double score) =>
        new(repository, commit, reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetString(4), reader.GetString(5), score);

    private static byte[] Serialize(float[] values)
    {
        var bytes = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static string SanitizeFts(string value) => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => '"' + x.Replace("\"", "", StringComparison.Ordinal) + '"'));
}
