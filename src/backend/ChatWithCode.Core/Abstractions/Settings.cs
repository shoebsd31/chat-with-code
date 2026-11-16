using ChatWithCode.Core.Domain;

namespace ChatWithCode.Core.Abstractions;

/// <summary>
/// The settings that apply to one Azure DevOps project: the project override where it sets a
/// value, otherwise the organization default. Secrets are decrypted and must never leave the server.
/// </summary>
public sealed record EffectiveModelSettings
{
    public required ModelProviderKind Provider { get; init; }
    public required Uri AzureOpenAIEndpoint { get; init; }
    public string? ChatDeployment { get; init; }
    public required string EmbeddingDeployment { get; init; }
    public required int EmbeddingDimensions { get; init; }
    public bool UseManagedIdentity { get; init; }
    public string? ApiKey { get; init; }

    public required VectorStoreKind VectorStore { get; init; }
    public Uri? AzureSearchEndpoint { get; init; }
    public string? AzureSearchIndexName { get; init; }
    public string? AzureSearchApiKey { get; init; }

    /// <summary>Identifies the embedding model an index was built with. A change means a full re-index.</summary>
    public string EmbeddingModelId => $"azure-openai:{AzureOpenAIEndpoint.Host}/{EmbeddingDeployment}";

    // Keep secrets out of logs and exception messages.
    public override string ToString() =>
        $"{Provider} {AzureOpenAIEndpoint} embed={EmbeddingDeployment}({EmbeddingDimensions}) chat={ChatDeployment} store={VectorStore}";
}

public interface IModelSettingsResolver
{
    /// <summary>Settings for a project. Throws <see cref="SettingsIncompleteException"/> when required values are missing.</summary>
    Task<EffectiveModelSettings> ResolveAsync(Guid adoProjectId, CancellationToken cancellationToken);
}

/// <summary>Required settings are missing. The message lists them, so admins know what to fill in.</summary>
public sealed class SettingsIncompleteException : Exception
{
    public SettingsIncompleteException()
    {
    }

    public SettingsIncompleteException(string message)
        : base(message)
    {
    }

    public SettingsIncompleteException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public static SettingsIncompleteException For(IReadOnlyList<string> missing) =>
        new($"Chat with Code isn't fully configured. Missing: {string.Join(", ", missing)}. " +
            "Set them in the organization settings (or the project override).");
}

/// <summary>Encrypts secrets (API keys) for storage. Backed by ASP.NET Data Protection.</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedValue);
}

/// <summary>Creates embedders for the configured provider.</summary>
public interface IEmbedderFactory
{
    IEmbedder Create(EffectiveModelSettings settings);
}

/// <summary>Creates the configured vector store.</summary>
public interface IVectorStoreFactory
{
    IVectorStore Create(EffectiveModelSettings settings);
}

/// <summary>Creates chat models for the configured provider.</summary>
public interface IChatModelFactory
{
    /// <summary>Throws <see cref="SettingsIncompleteException"/> when no chat deployment is configured.</summary>
    IChatModel Create(EffectiveModelSettings settings);
}
