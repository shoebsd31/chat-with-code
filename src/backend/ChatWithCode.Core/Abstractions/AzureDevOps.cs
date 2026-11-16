namespace ChatWithCode.Core.Abstractions;

/// <summary>Read-only access to Azure DevOps Git repositories, as the backend's service principal.</summary>
public interface IAdoGitClient
{
    /// <summary>Find a repository by project and repository name (or id). Null when it doesn't exist or isn't visible.</summary>
    Task<AdoRepository?> GetRepositoryAsync(string project, string repository, CancellationToken cancellationToken);

    /// <summary>Commit id at the tip of a branch (full ref name, e.g. refs/heads/main). Null when the branch doesn't exist.</summary>
    Task<string?> GetBranchHeadAsync(Guid repositoryId, string branchRef, CancellationToken cancellationToken);

    /// <summary>Every file (blob) in the repository at a commit, with sizes.</summary>
    Task<IReadOnlyList<AdoTreeEntry>> GetFilesAsync(Guid repositoryId, string commitId, CancellationToken cancellationToken);

    /// <summary>Raw content of a file by its blob id.</summary>
    Task<byte[]> GetBlobAsync(Guid repositoryId, string blobId, CancellationToken cancellationToken);
}

public sealed record AdoRepository(Guid Id, string Name, Guid ProjectId, string ProjectName, string? DefaultBranch);

/// <summary>A file in a repository tree. Path is repository-relative without a leading slash.</summary>
public sealed record AdoTreeEntry(string Path, string BlobId, long Size);

/// <summary>Azure DevOps refused access. The message says what to grant.</summary>
public sealed class AdoAccessException : Exception
{
    public AdoAccessException()
    {
    }

    public AdoAccessException(string message)
        : base(message)
    {
    }

    public AdoAccessException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
