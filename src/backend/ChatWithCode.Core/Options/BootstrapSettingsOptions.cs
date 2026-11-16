using ChatWithCode.Core.Domain;

namespace ChatWithCode.Core.Options;

/// <summary>
/// Optional starting values for the organization defaults, read from configuration
/// (the "AzureOpenAI" and "VectorStore" sections). They are saved once, when no organization
/// settings exist yet; after that, settings are managed in Azure DevOps and these are ignored.
/// </summary>
public sealed class AzureOpenAIBootstrapOptions
{
    public const string SectionName = "AzureOpenAI";

    public string? Endpoint { get; set; }
    public string? ApiKey { get; set; }
    public bool UseManagedIdentity { get; set; }
    public string? ChatDeployment { get; set; }
    public string? EmbeddingDeployment { get; set; }
    public int EmbeddingDimensions { get; set; } = 1536;
}

public sealed class VectorStoreBootstrapOptions
{
    public const string SectionName = "VectorStore";

    public VectorStoreKind Kind { get; set; } = VectorStoreKind.PgVector;
    public string? AzureSearchEndpoint { get; set; }
    public string? AzureSearchIndexName { get; set; }
    public string? AzureSearchApiKey { get; set; }
}
