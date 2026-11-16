using System.Globalization;
using ChatWithCode.Core.Abstractions;
using ChatWithCode.Core.Chat;
using ChatWithCode.Data;
using ChatWithCode.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ChatWithCode.VectorStores.PgVector;

/// <summary>
/// Stores chunks in the code_chunks table of the backend's own PostgreSQL database
/// (pgvector HNSW index for vectors, generated tsvector + GIN index for keywords).
/// </summary>
public sealed class PgVectorStore(ChatWithCodeDbContext db) : IVectorStore
{
    public Task EnsureIndexAsync(IndexSpec spec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Dimensions != CodeChunkRecord.Dimensions)
        {
            throw new InvalidOperationException(
                $"The pgvector store uses {CodeChunkRecord.Dimensions}-dimension vectors, but the embedding settings ask for " +
                $"{spec.Dimensions}. Set the embedding dimensions to {CodeChunkRecord.Dimensions} (text-embedding-3-small and " +
                "-large both support it).");
        }

        return Task.CompletedTask;
    }

    public async Task UpsertAsync(IReadOnlyList<CodeChunk> chunks, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        if (chunks.Count == 0)
        {
            return;
        }

        // Replace by id: remove any existing rows with the same ids, then insert.
        var ids = chunks.Select(c => c.Id).ToList();
        await db.CodeChunks.Where(c => ids.Contains(c.Id)).ExecuteDeleteAsync(cancellationToken);

        db.CodeChunks.AddRange(chunks.Select(c => new CodeChunkRecord
        {
            Id = c.Id,
            RepositoryId = c.Target.RepositoryId,
            Branch = c.Target.Branch,
            Path = c.Path,
            Language = c.Language,
            StartLine = c.StartLine,
            EndLine = c.EndLine,
            Symbol = c.Symbol,
            Content = c.Content,
            CommitId = c.CommitId,
            Embedding = new Pgvector.Vector(c.Embedding),
        }));
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear(); // don't keep thousands of vectors in memory
    }

    public async Task DeleteFilesAsync(RepoBranch target, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(paths);
        foreach (var batch in paths.Chunk(500))
        {
            await db.CodeChunks
                .Where(c => c.RepositoryId == target.RepositoryId && c.Branch == target.Branch && batch.Contains(c.Path))
                .ExecuteDeleteAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Vector search (HNSW, cosine) and keyword search (tsvector) over the allowed branches,
    /// merged with reciprocal rank fusion: a chunk's score is the sum of 1/(60 + rank) over the
    /// lists it appears in, so chunks found both ways rise to the top.
    /// </summary>
    public async Task<IReadOnlyList<SearchHit>> HybridSearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.AllowedTargets.Count == 0)
        {
            return [];
        }

        var vector = "[" + string.Join(',', query.Embedding.Select(f => f.ToString("R", CultureInfo.InvariantCulture))) + "]";
        var tsQuery = KeywordQuery.ToTsQuery(query.Text);
        var candidates = Math.Max(query.Top, 40);

        const string sql = """
            WITH allowed AS (
                SELECT * FROM unnest(@repos::uuid[], @branches::text[]) AS a(repository_id, branch)
            ),
            vec AS (
                SELECT c.id, row_number() OVER (ORDER BY c.embedding <=> @vector::vector) AS rank
                FROM code_chunks c
                WHERE (c.repository_id, c.branch) IN (SELECT repository_id, branch FROM allowed)
                ORDER BY c.embedding <=> @vector::vector
                LIMIT @candidates
            ),
            kw AS (
                SELECT c.id, row_number() OVER (ORDER BY ts_rank_cd(c.search_vector, q) DESC) AS rank
                FROM code_chunks c, to_tsquery('simple', coalesce(@tsquery, '')) q
                WHERE @tsquery IS NOT NULL
                  AND c.search_vector @@ q
                  AND (c.repository_id, c.branch) IN (SELECT repository_id, branch FROM allowed)
                ORDER BY ts_rank_cd(c.search_vector, q) DESC
                LIMIT @candidates
            )
            SELECT c.id, c.repository_id, c.branch, c.path, c.language, c.start_line, c.end_line, c.symbol,
                   c.content, c.commit_id,
                   coalesce(1.0 / (60 + vec.rank), 0) + coalesce(1.0 / (60 + kw.rank), 0) AS score
            FROM code_chunks c
            LEFT JOIN vec ON vec.id = c.id
            LEFT JOIN kw ON kw.id = c.id
            WHERE vec.id IS NOT NULL OR kw.id IS NOT NULL
            ORDER BY score DESC
            LIMIT @top
            """;

        var parameters = new object[]
        {
            new NpgsqlParameter("repos", query.AllowedTargets.Select(t => t.RepositoryId).ToArray()),
            new NpgsqlParameter("branches", query.AllowedTargets.Select(t => t.Branch).ToArray()),
            new NpgsqlParameter("vector", vector),
            new NpgsqlParameter("tsquery", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)tsQuery ?? DBNull.Value },
            new NpgsqlParameter("candidates", candidates),
            new NpgsqlParameter("top", query.Top),
        };

        // Let the HNSW index keep scanning when the branch filter removes many neighbours (pgvector 0.8+).
        var strategy = db.Database.CreateExecutionStrategy();
        var rows = await strategy.ExecuteAsync(async ct =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlRawAsync("SET LOCAL hnsw.ef_search = 100; SET LOCAL hnsw.iterative_scan = relaxed_order;", ct);
            var result = await db.Database.SqlQueryRaw<HitRow>(sql, parameters).ToListAsync(ct);
            await transaction.CommitAsync(ct);
            return result;
        }, cancellationToken);

        return rows.Select(r => new SearchHit(
            new CodeChunk(r.Id, new RepoBranch(r.RepositoryId, r.Branch), r.Path, r.Language, r.StartLine, r.EndLine, r.Symbol, r.Content, r.CommitId, []),
            (double)r.Score)).ToList();
    }

    // Column names follow the snake_case convention used for the whole database.
    private sealed record HitRow(
        string Id, Guid RepositoryId, string Branch, string Path, string Language, int StartLine, int EndLine,
        string? Symbol, string Content, string CommitId, decimal Score);
}
