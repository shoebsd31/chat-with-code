using System.ClientModel;
using System.ClientModel.Primitives;
using Azure.AI.OpenAI;
using Azure.Identity;
using ChatWithCode.Core.Abstractions;
using ChatWithCode.Core.Domain;

namespace ChatWithCode.AzureOpenAI;

/// <summary>Builds Azure OpenAI clients from the effective settings (API key or managed identity).</summary>
public sealed class AzureOpenAIEmbedderFactory(HttpClient? transport = null) : IEmbedderFactory
{
    public IEmbedder Create(EffectiveModelSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Provider != ModelProviderKind.AzureOpenAI)
        {
            throw new NotSupportedException($"Model provider {settings.Provider} isn't supported yet.");
        }

        var client = CreateClient(settings, transport);
        return new AzureOpenAIEmbedder(client, settings.EmbeddingDeployment, settings.EmbeddingDimensions, settings.EmbeddingModelId);
    }

    internal static AzureOpenAIClient CreateClient(EffectiveModelSettings settings, HttpClient? transport)
    {
        var options = new AzureOpenAIClientOptions
        {
            RetryPolicy = new ClientRetryPolicy(maxRetries: 6),
            NetworkTimeout = TimeSpan.FromSeconds(100),
        };
        if (transport is not null)
        {
            options.Transport = new HttpClientPipelineTransport(transport);
        }

        if (settings.UseManagedIdentity)
        {
            return new AzureOpenAIClient(settings.AzureOpenAIEndpoint, new DefaultAzureCredential(), options);
        }

        var key = settings.ApiKey ?? throw new InvalidOperationException("No Azure OpenAI API key is configured.");
        return new AzureOpenAIClient(settings.AzureOpenAIEndpoint, new ApiKeyCredential(key), options);
    }
}
