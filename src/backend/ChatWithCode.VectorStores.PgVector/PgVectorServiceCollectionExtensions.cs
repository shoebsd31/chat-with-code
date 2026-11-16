using Microsoft.Extensions.DependencyInjection;

namespace ChatWithCode.VectorStores.PgVector;

public static class PgVectorServiceCollectionExtensions
{
    public static IServiceCollection AddPgVectorStore(this IServiceCollection services)
    {
        services.AddScoped<PgVectorStore>();
        return services;
    }
}
