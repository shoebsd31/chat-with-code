using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;

namespace ChatWithCode.Data;

/// <summary>
/// The app's managed identity in Azure. A user-assigned identity is chosen with AZURE_CLIENT_ID
/// (the same variable DefaultAzureCredential uses); without it, the system-assigned identity.
/// </summary>
public static class ManagedIdentity
{
    public static TokenCredential Credential(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var clientId = configuration["AZURE_CLIENT_ID"];
        return string.IsNullOrWhiteSpace(clientId)
            ? new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned)
            : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(clientId));
    }
}
