using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using ChatWithCode.Core.Abstractions;

namespace ChatWithCode.AzureDevOps;

/// <summary>
/// Azure DevOps Git REST API (api-version 7.1). The HttpClient's base address is the
/// organization URL and its handler adds the service principal's bearer token.
/// </summary>
public sealed class AdoGitClient(HttpClient http) : IAdoGitClient
{
    private const string ApiVersion = "api-version=7.1";

    public async Task<AdoRepository?> GetRepositoryAsync(string project, string repository, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);

        var url = $"{Escape(project)}/_apis/git/repositories/{Escape(repository)}?{ApiVersion}";
        using var response = await http.GetAsync(new Uri(url, UriKind.Relative), cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, $"repository '{project}/{repository}'", cancellationToken);
        var repo = await response.Content.ReadFromJsonAsync<RepositoryDto>(cancellationToken)
            ?? throw new InvalidOperationException("Azure DevOps returned an empty repository response.");
        return new AdoRepository(repo.Id, repo.Name, repo.Project.Id, repo.Project.Name, repo.DefaultBranch);
    }

    public async Task<string?> GetBranchHeadAsync(Guid repositoryId, string branchRef, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branchRef);
        var filter = branchRef.StartsWith("refs/", StringComparison.Ordinal) ? branchRef["refs/".Length..] : $"heads/{branchRef}";
        var url = $"_apis/git/repositories/{repositoryId}/refs?filter={Escape(filter)}&{ApiVersion}";
        var refs = await GetAsync<ListDto<RefDto>>(url, $"branches of repository {repositoryId}", cancellationToken);

        // The filter is a prefix match ("heads/main" also returns "heads/main-old"), so compare exactly.
        var fullName = "refs/" + filter;
        return refs.Value.FirstOrDefault(r => string.Equals(r.Name, fullName, StringComparison.Ordinal))?.ObjectId;
    }

    public async Task<IReadOnlyList<string>> GetBranchesAsync(Guid repositoryId, CancellationToken cancellationToken)
    {
        var refs = await GetAsync<ListDto<RefDto>>($"_apis/git/repositories/{repositoryId}/refs?filter=heads%2F&{ApiVersion}", $"branches of repository {repositoryId}", cancellationToken);
        return refs.Value.Select(r => r.Name).Order(StringComparer.Ordinal).ToList();
    }

    public async Task<IReadOnlyList<AdoTreeEntry>> GetFilesAsync(Guid repositoryId, string commitId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commitId);
        var commit = await GetAsync<CommitDto>($"_apis/git/repositories/{repositoryId}/commits/{commitId}?{ApiVersion}", $"commit {commitId}", cancellationToken);
        var tree = await GetAsync<TreeDto>($"_apis/git/repositories/{repositoryId}/trees/{commit.TreeId}?recursive=true&{ApiVersion}", $"file tree of commit {commitId}", cancellationToken);

        // Blobs only: skip folders ("tree") and submodules ("commit"). Mode 120000 is a symbolic link.
        return tree.TreeEntries
            .Where(e => e.GitObjectType == "blob" && e.Mode != "120000")
            .Select(e => new AdoTreeEntry(e.RelativePath.TrimStart('/'), e.ObjectId, e.Size))
            .ToList();
    }

    public async Task<byte[]> GetBlobAsync(Guid repositoryId, string blobId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blobId);
        var url = $"_apis/git/repositories/{repositoryId}/blobs/{blobId}?%24format=octetstream&{ApiVersion}";
        using var response = await http.GetAsync(new Uri(url, UriKind.Relative), cancellationToken);
        await EnsureSuccessAsync(response, $"file {blobId}", cancellationToken);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private async Task<T> GetAsync<T>(string url, string what, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(new Uri(url, UriKind.Relative), cancellationToken);
        await EnsureSuccessAsync(response, what, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
            ?? throw new InvalidOperationException($"Azure DevOps returned an empty response for {what}.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string what, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            // Azure DevOps answers some unauthenticated requests with a 203 sign-in page instead of 401.
            if (response.StatusCode == HttpStatusCode.NonAuthoritativeInformation)
            {
                throw new AdoAccessException(
                    $"Azure DevOps didn't accept the service principal's sign-in while reading {what}. " +
                    "Check AzureDevOps:ServicePrincipal (tenant, client id, secret).");
            }

            return;
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new AdoAccessException(
                $"Azure DevOps refused access to {what} ({(int)response.StatusCode}). Make sure the service principal " +
                "is added to the organization (Organization settings > Users) and has Read access to the repository.");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException(
            $"Azure DevOps returned {(int)response.StatusCode} {response.ReasonPhrase} for {what}: {Truncate(body, 300)}",
            inner: null,
            response.StatusCode);
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";

    private sealed record ListDto<T>([property: JsonPropertyName("value")] List<T> Value);

    private sealed record RepositoryDto(Guid Id, string Name, string? DefaultBranch, ProjectDto Project);

    private sealed record ProjectDto(Guid Id, string Name);

    private sealed record RefDto(string Name, string ObjectId);

    private sealed record CommitDto(string CommitId, string TreeId);

    private sealed record TreeDto(List<TreeEntryDto> TreeEntries);

    private sealed record TreeEntryDto(string RelativePath, string GitObjectType, string ObjectId, long Size, string? Mode);
}
