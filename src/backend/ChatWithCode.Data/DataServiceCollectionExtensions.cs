using ChatWithCode.Data.Queue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ChatWithCode.Data;

public static class DataServiceCollectionExtensions
{
    public const string ConnectionStringName = "Metadata";

    /// <summary>Register the metadata database and the job queue.</summary>
    public static IServiceCollection AddChatWithCodeData(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Connection string 'ConnectionStrings:{ConnectionStringName}' is missing. " +
                "Set it in appsettings.json or the ConnectionStrings__Metadata environment variable.");
        }

        services.AddDbContext<ChatWithCodeDbContext>(o => ConfigureDb(o, connectionString));
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IndexJobQueue>();
        services.AddScoped<DatabaseInitializer>();
        return services;
    }

    internal static void ConfigureDb(DbContextOptionsBuilder options, string connectionString) =>
        options
            .UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure(5))
            .UseSnakeCaseNamingConvention();
}
