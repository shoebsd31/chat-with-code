using ChatWithCode.Core.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace ChatWithCode.Core.Abstractions;

/// <summary>
/// Resolves the vector store chosen in the settings. Each store registers itself (or a factory
/// for itself) as a keyed service under its <see cref="VectorStoreKind"/>, so adding a store
/// needs no change here.
/// </summary>
public sealed class KeyedVectorStoreFactory(IServiceProvider services) : IVectorStoreFactory
{
    public IVectorStore Create(EffectiveModelSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // Stores that need the settings to connect (e.g. Azure AI Search) register a factory;
        // stores that live in the backend's own database (pgvector) register the store itself.
        if (services.GetKeyedService<IVectorStoreFactory>(settings.VectorStore) is { } factory)
        {
            return factory.Create(settings);
        }

        return services.GetKeyedService<IVectorStore>(settings.VectorStore)
            ?? throw new NotSupportedException($"Vector store {settings.VectorStore} isn't available in this build.");
    }
}
