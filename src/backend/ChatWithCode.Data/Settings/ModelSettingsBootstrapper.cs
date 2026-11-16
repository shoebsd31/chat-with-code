using ChatWithCode.Core.Abstractions;
using ChatWithCode.Core.Domain;
using ChatWithCode.Core.Options;
using ChatWithCode.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChatWithCode.Data.Settings;

/// <summary>
/// Saves the organization defaults from configuration (.env) the first time the backend starts.
/// Once organization settings exist, configuration is ignored so it never overwrites what admins
/// changed in Azure DevOps.
/// </summary>
public sealed class ModelSettingsBootstrapper(
    ChatWithCodeDbContext db,
    ISecretProtector secrets,
    TimeProvider clock,
    IOptions<AzureDevOpsOptions> ado,
    IOptions<AzureOpenAIBootstrapOptions> openAI,
    IOptions<VectorStoreBootstrapOptions> vectorStore,
    ILogger<ModelSettingsBootstrapper> logger)
{
    public async Task<bool> ApplyAsync(CancellationToken cancellationToken = default)
    {
        var o = openAI.Value;
        if (string.IsNullOrWhiteSpace(o.Endpoint))
        {
            return false;
        }

        var org = await db.Organizations.SingleAsync(x => x.Name == ado.Value.Organization, cancellationToken);
        if (await db.ModelSettings.AnyAsync(s => s.OrganizationId == org.Id && s.ProjectId == null, cancellationToken))
        {
            logger.LogDebug("Organization settings already exist; ignoring AzureOpenAI configuration values");
            return false;
        }

        var v = vectorStore.Value;
        db.ModelSettings.Add(new ModelSettings
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = org.Id,
            Scope = SettingScope.Organization,
            Provider = ModelProviderKind.AzureOpenAI,
            AzureOpenAIEndpoint = o.Endpoint.Trim(),
            ChatDeployment = Blank(o.ChatDeployment),
            EmbeddingDeployment = Blank(o.EmbeddingDeployment),
            EmbeddingDimensions = o.EmbeddingDimensions,
            UseManagedIdentity = o.UseManagedIdentity,
            ApiKeyProtected = Blank(o.ApiKey) is { } key ? secrets.Protect(key) : null,
            VectorStore = v.Kind,
            AzureSearchEndpoint = Blank(v.AzureSearchEndpoint),
            AzureSearchIndexName = Blank(v.AzureSearchIndexName),
            AzureSearchApiKeyProtected = Blank(v.AzureSearchApiKey) is { } searchKey ? secrets.Protect(searchKey) : null,
            UpdatedAt = clock.GetUtcNow(),
            UpdatedBy = "configuration",
        });
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Saved organization defaults from configuration (Azure OpenAI endpoint {Endpoint})", o.Endpoint);
        return true;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
