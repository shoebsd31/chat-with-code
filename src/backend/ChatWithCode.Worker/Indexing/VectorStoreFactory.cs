using ChatWithCode.Core.Abstractions;
using ChatWithCode.Core.Domain;
using ChatWithCode.VectorStores.PgVector;

namespace ChatWithCode.Worker.Indexing;

/// <summary>Resolves the vector store chosen in the settings.</summary>
public sealed class VectorStoreFactory(IServiceProvider services) : IVectorStoreFactory
{
    public IVectorStore Create(EffectiveModelSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.VectorStore switch
        {
            VectorStoreKind.PgVector => services.GetRequiredService<PgVectorStore>(),
            VectorStoreKind.AzureAISearch => throw new NotSupportedException(
                "Azure AI Search is added in phase 5. Use the PgVector store for now."),
            _ => throw new NotSupportedException($"Vector store {settings.VectorStore} isn't supported."),
        };
    }
}
