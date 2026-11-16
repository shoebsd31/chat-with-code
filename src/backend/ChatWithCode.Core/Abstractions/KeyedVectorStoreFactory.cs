using ChatWithCode.Core.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace ChatWithCode.Core.Abstractions;

/// <summary>
/// Resolves the vector store chosen in the settings. Each store registers itself as a keyed
/// service under its <see cref="VectorStoreKind"/>, so adding a store needs no change here.
/// </summary>
public sealed class KeyedVectorStoreFactory(IServiceProvider services) : IVectorStoreFactory
{
    public IVectorStore Create(EffectiveModelSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return services.GetKeyedService<IVectorStore>(settings.VectorStore)
            ?? throw new NotSupportedException(settings.VectorStore == VectorStoreKind.AzureAISearch
                ? "Azure AI Search is added in phase 5. Use the PgVector store for now."
                : $"Vector store {settings.VectorStore} isn't available in this build.");
    }
}
