using Azure.Core;
using Azure.Identity;
using ChatWithCode.Core.Abstractions;
using ChatWithCode.Core.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

namespace ChatWithCode.AzureDevOps;

public static class AzureDevOpsServiceCollectionExtensions
{
    /// <summary>Register the Git client that reads repositories as the configured service principal.</summary>
    public static IServiceCollection AddAzureDevOps(this IServiceCollection services)
    {
        services.AddSingleton<TokenCredential>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<AzureDevOpsOptions>>().Value;
            var principal = options.ServicePrincipal;
            if (!principal.IsConfigured)
            {
                return new MissingServicePrincipalCredential();
            }

            return new ClientSecretCredential(principal.TenantId, principal.ClientId, principal.ClientSecret);
        });

        services.AddTransient<ServicePrincipalTokenHandler>();
        services.AddHttpClient<IAdoGitClient, AdoGitClient>((sp, http) =>
            {
                http.BaseAddress = sp.GetRequiredService<IOptions<AzureDevOpsOptions>>().Value.OrganizationUrl;
                http.DefaultRequestHeaders.UserAgent.ParseAdd("ChatWithCode/1.0");
            })
            .AddHttpMessageHandler<ServicePrincipalTokenHandler>()
            .AddStandardResilienceHandler(o =>
            {
                // Large repositories have big tree listings; give each attempt time to finish.
                o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(60);
                o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(120);
                o.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(4);
            });

        // Calls made as the signed-in user: no service principal token, the user's own token is added per request.
        services.AddHttpClient<IAdoUserClient, AdoUserClient>((sp, http) =>
            {
                http.BaseAddress = sp.GetRequiredService<IOptions<AzureDevOpsOptions>>().Value.OrganizationUrl;
                http.DefaultRequestHeaders.UserAgent.ParseAdd("ChatWithCode/1.0");
            })
            .AddStandardResilienceHandler();

        return services;
    }

    /// <summary>Fails with setup instructions when no service principal is configured.</summary>
    private sealed class MissingServicePrincipalCredential : TokenCredential
    {
        private const string Message =
            "No service principal is configured for reading Azure DevOps repositories. Set AzureDevOps:ServicePrincipal " +
            "TenantId, ClientId and ClientSecret (ADO_SP_TENANT_ID, ADO_SP_CLIENT_ID, ADO_SP_CLIENT_SECRET in .env).";

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new AdoAccessException(Message);

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new AdoAccessException(Message);
    }
}
