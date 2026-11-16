using ChatWithCode.Core.Abstractions;
using ChatWithCode.Core.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace ChatWithCode.VectorStores.PgVector;

public static class PgVectorServiceCollectionExtensions
{
    public static IServiceCollection AddPgVectorStore(this IServiceCollection services)
    {
        services.AddKeyedScoped<IVectorStore, PgVectorStore>(VectorStoreKind.PgVector);
        return services;
    }
}
