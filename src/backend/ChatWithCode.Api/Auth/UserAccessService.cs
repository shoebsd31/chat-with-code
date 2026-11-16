using System.Security.Cryptography;
using System.Text;
using ChatWithCode.Core.Abstractions;
using Microsoft.Extensions.Caching.Memory;

namespace ChatWithCode.Api.Auth;

/// <summary>The signed-in user and the repositories they can read, as Azure DevOps reported them.</summary>
public sealed record UserAccess(AdoUser User, IReadOnlyDictionary<Guid, ReadableRepository> ReadableRepositories)
{
    public bool CanRead(Guid adoRepositoryId) => ReadableRepositories.ContainsKey(adoRepositoryId);
}

/// <summary>
/// Confirms the user with Azure DevOps using their own access token and caches the answer for
/// a few minutes, so permission changes in Azure DevOps take effect quickly without calling
/// Azure DevOps on every request.
/// </summary>
public sealed class UserAccessService(IAdoUserClient ado, IMemoryCache cache)
{
    public static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    public async Task<UserAccess> GetAsync(string userAccessToken, CancellationToken cancellationToken)
    {
        // Key on a hash, so tokens are never kept in memory as cache keys.
        var key = "access:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userAccessToken)));
        if (cache.TryGetValue(key, out UserAccess? cached) && cached is not null)
        {
            return cached;
        }

        var user = await ado.GetUserAsync(userAccessToken, cancellationToken);
        var repositories = await ado.GetReadableRepositoriesAsync(userAccessToken, cancellationToken);
        var access = new UserAccess(user, repositories.ToDictionary(r => r.Id));
        cache.Set(key, access, CacheDuration);
        return access;
    }
}

/// <summary>Runs before every user-facing endpoint: no Azure DevOps sign-in, no access.</summary>
public sealed class RequireUserAccessFilter(UserAccessService access, ILogger<RequireUserAccessFilter> logger) : IEndpointFilter
{
    public const string HeaderName = "X-AzureDevOps-Token";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var http = context.HttpContext;
        var token = http.Request.Headers[HeaderName].ToString();
        if (string.IsNullOrWhiteSpace(token))
        {
            return Results.Problem($"The request has no Azure DevOps sign-in ({HeaderName} header). Reload the page.",
                statusCode: StatusCodes.Status401Unauthorized, title: "Not signed in");
        }

        try
        {
            http.Items[typeof(UserAccess)] = await access.GetAsync(token, http.RequestAborted);
        }
        catch (AdoAccessException ex)
        {
            logger.LogInformation("Azure DevOps rejected a user token: {Message}", ex.Message);
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status401Unauthorized, title: "Not signed in");
        }

        return await next(context);
    }
}

public static class UserAccessHttpContextExtensions
{
    public static UserAccess GetUserAccess(this HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return http.Items[typeof(UserAccess)] as UserAccess
            ?? throw new InvalidOperationException("RequireUserAccessFilter didn't run for this endpoint.");
    }
}
