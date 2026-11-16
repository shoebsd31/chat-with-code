namespace ChatWithCode.Core.Abstractions;

/// <summary>The signed-in Azure DevOps user, as confirmed by Azure DevOps itself.</summary>
public sealed record AdoUser(string Id, string DisplayName);

/// <summary>A repository the user is allowed to read.</summary>
public sealed record ReadableRepository(Guid Id, string Name, Guid ProjectId, string ProjectName);

/// <summary>
/// Calls Azure DevOps with the signed-in user's own access token (from the extension).
/// The token is passed through as-is and never decoded: Azure DevOps answers only for
/// members of the organization, and only lists repositories the user can read.
/// </summary>
public interface IAdoUserClient
{
    /// <summary>The user behind the token. Throws <see cref="AdoAccessException"/> when Azure DevOps rejects it.</summary>
    Task<AdoUser> GetUserAsync(string userAccessToken, CancellationToken cancellationToken);

    /// <summary>Every repository in the organization that the user can read.</summary>
    Task<IReadOnlyList<ReadableRepository>> GetReadableRepositoriesAsync(string userAccessToken, CancellationToken cancellationToken);
}
