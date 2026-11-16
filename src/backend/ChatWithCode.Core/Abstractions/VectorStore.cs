namespace ChatWithCode.Core.Abstractions;

/// <summary>Stores code chunks with their vectors. v1 implementations: pgvector and Azure AI Search.</summary>
public interface IVectorStore
{
    Task EnsureIndexAsync(IndexSpec spec, CancellationToken cancellationToken);
    Task UpsertAsync(IReadOnlyList<CodeChunk> chunks, CancellationToken cancellationToken);
    Task DeleteFilesAsync(RepoBranch target, IReadOnlyList<string> paths, CancellationToken cancellationToken);

    /// <summary>Remove everything indexed for a branch (when it's taken out of the index).</summary>
    Task DeleteBranchAsync(RepoBranch target, CancellationToken cancellationToken);
    Task<IReadOnlyList<SearchHit>> HybridSearchAsync(SearchQuery query, CancellationToken cancellationToken);
}

/// <summary>The embedding model and vector size an index was built with.</summary>
public sealed record IndexSpec(string EmbeddingModel, int Dimensions);

/// <summary>A branch of a repository, identified by Azure DevOps ids.</summary>
public sealed record RepoBranch(Guid RepositoryId, string Branch);

public sealed record CodeChunk(
    string Id,
    RepoBranch Target,
    string Path,
    string Language,
    int StartLine,
    int EndLine,
    string? Symbol,
    string Content,
    string CommitId,
    float[] Embedding);

public sealed record SearchQuery(
    string Text,
    float[] Embedding,
    IReadOnlyList<RepoBranch> AllowedTargets,
    int Top);

public sealed record SearchHit(CodeChunk Chunk, double Score);
