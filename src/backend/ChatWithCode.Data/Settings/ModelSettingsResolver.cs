using ChatWithCode.Core.Abstractions;
using ChatWithCode.Core.Domain;
using ChatWithCode.Core.Options;
using ChatWithCode.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ChatWithCode.Data.Settings;

/// <summary>Merges a project's override over the organization defaults and decrypts secrets.</summary>
public sealed class ModelSettingsResolver(
    ChatWithCodeDbContext db,
    ISecretProtector secrets,
    IOptions<AzureDevOpsOptions> ado) : IModelSettingsResolver
{
    public async Task<EffectiveModelSettings> ResolveAsync(Guid adoProjectId, CancellationToken cancellationToken)
    {
        var organization = ado.Value.Organization;
        var rows = await db.ModelSettings
            .AsNoTracking()
            .Join(db.Organizations.Where(o => o.Name == organization), s => s.OrganizationId, o => o.Id, (s, _) => s)
            .Where(s => s.ProjectId == null || s.ProjectId == adoProjectId)
            .ToListAsync(cancellationToken);

        var org = rows.FirstOrDefault(r => r.ProjectId is null);
        var project = rows.FirstOrDefault(r => r.ProjectId == adoProjectId);
        return Merge(org, project, secrets);
    }

    /// <summary>Project value if set, else organization value. Validates that required values are present.</summary>
    public static EffectiveModelSettings Merge(ModelSettings? org, ModelSettings? project, ISecretProtector secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        T? Pick<T>(Func<ModelSettings, T?> value) where T : class => (project is null ? null : value(project)) ?? (org is null ? null : value(org));
        T? PickValue<T>(Func<ModelSettings, T?> value) where T : struct => (project is null ? null : value(project)) ?? (org is null ? null : value(org));

        var missing = new List<string>();
        var provider = PickValue(s => s.Provider) ?? ModelProviderKind.AzureOpenAI;
        var endpoint = Pick(s => s.AzureOpenAIEndpoint);
        var embeddingDeployment = Pick(s => s.EmbeddingDeployment);
        var useManagedIdentity = PickValue(s => s.UseManagedIdentity) ?? false;
        var protectedKey = Pick(s => s.ApiKeyProtected);
        var vectorStore = PickValue(s => s.VectorStore) ?? VectorStoreKind.PgVector;
        var searchEndpoint = Pick(s => s.AzureSearchEndpoint);
        var searchIndex = Pick(s => s.AzureSearchIndexName);
        var protectedSearchKey = Pick(s => s.AzureSearchApiKeyProtected);

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri))
        {
            missing.Add("Azure OpenAI endpoint");
        }

        if (string.IsNullOrWhiteSpace(embeddingDeployment))
        {
            missing.Add("embedding deployment");
        }

        if (!useManagedIdentity && protectedKey is null)
        {
            missing.Add("Azure OpenAI API key (or turn on managed identity)");
        }

        Uri? searchUri = null;
        if (vectorStore == VectorStoreKind.AzureAISearch)
        {
            if (!Uri.TryCreate(searchEndpoint, UriKind.Absolute, out searchUri))
            {
                missing.Add("Azure AI Search endpoint");
            }

            if (string.IsNullOrWhiteSpace(searchIndex))
            {
                missing.Add("Azure AI Search index name");
            }

            if (!useManagedIdentity && protectedSearchKey is null)
            {
                missing.Add("Azure AI Search API key (or turn on managed identity)");
            }
        }

        if (missing.Count > 0)
        {
            throw SettingsIncompleteException.For(missing);
        }

        return new EffectiveModelSettings
        {
            Provider = provider,
            AzureOpenAIEndpoint = endpointUri!,
            ChatDeployment = Pick(s => s.ChatDeployment),
            EmbeddingDeployment = embeddingDeployment!,
            EmbeddingDimensions = PickValue(s => s.EmbeddingDimensions) ?? 1536,
            UseManagedIdentity = useManagedIdentity,
            ApiKey = protectedKey is null ? null : secrets.Unprotect(protectedKey),
            VectorStore = vectorStore,
            AzureSearchEndpoint = searchUri,
            AzureSearchIndexName = searchIndex,
            AzureSearchApiKey = protectedSearchKey is null ? null : secrets.Unprotect(protectedSearchKey),
        };
    }
}
