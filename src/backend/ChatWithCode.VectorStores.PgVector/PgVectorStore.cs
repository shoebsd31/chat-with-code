using ChatWithCode.Core.Abstractions;
using ChatWithCode.Data;
using ChatWithCode.Data.Entities;
using Microsoft.EntityFrameworkCore;

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

    public Task<IReadOnlyList<SearchHit>> HybridSearchAsync(SearchQuery query, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Search is added in phase 3.");
}
