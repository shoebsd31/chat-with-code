using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ChatWithCode.Core.Abstractions;

namespace ChatWithCode.AzureDevOps;

/// <summary>Azure DevOps REST calls made as the signed-in user (their extension access token).</summary>
public sealed class AdoUserClient(HttpClient http) : IAdoUserClient
{
    public async Task<AdoUser> GetUserAsync(string userAccessToken, CancellationToken cancellationToken)
    {
        var data = await GetAsync<ConnectionDataDto>("_apis/connectionData?api-version=7.1-preview", userAccessToken, cancellationToken);
        var user = data.AuthenticatedUser;
        if (user is null || user.Id == Guid.Empty)
        {
            throw new AdoAccessException("Azure DevOps didn't confirm who the user is. Sign in to Azure DevOps again.");
        }

        // The sign-in name (usually the email address) is in the "Account" property.
        string? email = null;
        if (user.Properties is { ValueKind: JsonValueKind.Object } props &&
            props.TryGetProperty("Account", out var account) &&
            account.TryGetProperty("$value", out var value) && value.ValueKind == JsonValueKind.String)
        {
            email = value.GetString();
        }

        return new AdoUser(user.Id.ToString(), user.ProviderDisplayName ?? user.CustomDisplayName ?? "Azure DevOps user", email);
    }

    public async Task<IReadOnlyList<ReadableRepository>> GetReadableRepositoriesAsync(string userAccessToken, CancellationToken cancellationToken)
    {
        // Organization-level list: Azure DevOps returns only repositories the caller can read.
        var list = await GetAsync<ListDto<RepositoryDto>>("_apis/git/repositories?api-version=7.1", userAccessToken, cancellationToken);
        return list.Value
            .Where(r => r.IsDisabled != true)
            .Select(r => new ReadableRepository(r.Id, r.Name, r.Project.Id, r.Project.Name, r.DefaultBranch))
            .ToList();
    }

    private async Task<T> GetAsync<T>(string url, string userAccessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", userAccessToken);
        using var response = await http.SendAsync(request, cancellationToken);

        // 203 is Azure DevOps' sign-in page for a token it doesn't accept.
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NonAuthoritativeInformation)
        {
            throw new AdoAccessException("Azure DevOps didn't accept your sign-in for this organization. Reload the page and try again.");
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
            ?? throw new InvalidOperationException("Azure DevOps returned an empty response.");
    }

    private sealed record ConnectionDataDto(IdentityDto? AuthenticatedUser);

    private sealed record IdentityDto(Guid Id, string? ProviderDisplayName, string? CustomDisplayName, JsonElement? Properties);

    private sealed record ListDto<T>(List<T> Value);

    private sealed record RepositoryDto(Guid Id, string Name, bool? IsDisabled, string? DefaultBranch, ProjectDto Project);

    private sealed record ProjectDto(Guid Id, string Name);
}
