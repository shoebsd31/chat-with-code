using Azure;
using Azure.Core.Pipeline;
using Azure.Identity;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using ChatWithCode.Core.Abstractions;
using ChatWithCode.Core.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace ChatWithCode.VectorStores.AzureAISearch;

/// <summary>Creates the Azure AI Search store from the effective settings (admin key or managed identity).</summary>
public sealed class AzureAISearchStoreFactory(HttpClient? transport = null) : IVectorStoreFactory
{
    public IVectorStore Create(EffectiveModelSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var endpoint = settings.AzureSearchEndpoint ?? throw SettingsIncompleteException.For(["Azure AI Search endpoint"]);
        var indexName = settings.AzureSearchIndexName ?? throw SettingsIncompleteException.For(["Azure AI Search index name"]);

        var options = new SearchClientOptions();
        if (transport is not null)
        {
            options.Transport = new HttpClientTransport(transport);
        }

        var client = settings.UseManagedIdentity
            ? new SearchIndexClient(endpoint, new DefaultAzureCredential(), options)
            : new SearchIndexClient(endpoint, new AzureKeyCredential(settings.AzureSearchApiKey ?? throw SettingsIncompleteException.For(["Azure AI Search API key"])), options);
        return new AzureAISearchStore(client, indexName);
    }
}

public static class AzureAISearchServiceCollectionExtensions
{
    /// <summary>Register the store under its kind; KeyedVectorStoreFactory picks it when the settings say AzureAISearch.</summary>
    public static IServiceCollection AddAzureAISearchStore(this IServiceCollection services)
    {
        services.AddKeyedSingleton<IVectorStoreFactory>(VectorStoreKind.AzureAISearch, new AzureAISearchStoreFactory());
        return services;
    }
}
