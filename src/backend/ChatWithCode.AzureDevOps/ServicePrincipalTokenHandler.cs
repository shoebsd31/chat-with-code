using System.Net.Http.Headers;
using Azure.Core;

namespace ChatWithCode.AzureDevOps;

/// <summary>Adds an Entra access token for Azure DevOps to every request. Tokens are cached by Azure.Identity.</summary>
public sealed class ServicePrincipalTokenHandler(TokenCredential credential) : DelegatingHandler
{
    /// <summary>The Azure DevOps resource id; the same for every organization.</summary>
    public const string AzureDevOpsScope = "499b84ac-1321-427f-aa17-267ca6975798/.default";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var token = await credential.GetTokenAsync(new TokenRequestContext([AzureDevOpsScope]), cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        return await base.SendAsync(request, cancellationToken);
    }
}
