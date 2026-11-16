using ChatWithCode.Core.Abstractions;
using ChatWithCode.Core.Options;
using ChatWithCode.Data.Queue;
using ChatWithCode.Data.Settings;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ChatWithCode.Data;

public static class DataServiceCollectionExtensions
{
    public const string ConnectionStringName = "Metadata";

    /// <summary>Register the metadata database, the job queue, settings and secret encryption.</summary>
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

        // The API and the worker share one key ring (a mounted volume), so either can decrypt API keys.
        var security = configuration.GetSection(SecurityOptions.SectionName).Get<SecurityOptions>() ?? new SecurityOptions();
        services.AddDataProtection()
            .SetApplicationName("ChatWithCode")
            .PersistKeysToFileSystem(new DirectoryInfo(security.DataProtectionKeysPath));
        services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();

        services.AddOptions<AzureOpenAIBootstrapOptions>().Bind(configuration.GetSection(AzureOpenAIBootstrapOptions.SectionName));
        services.AddOptions<VectorStoreBootstrapOptions>().Bind(configuration.GetSection(VectorStoreBootstrapOptions.SectionName));
        services.AddScoped<IModelSettingsResolver, ModelSettingsResolver>();
        services.AddScoped<ModelSettingsBootstrapper>();
        return services;
    }

    /// <summary>Shared database configuration (also used by tests and the design-time factory).</summary>
    public static DbContextOptionsBuilder ConfigureDb(DbContextOptionsBuilder options, string connectionString, bool retryOnFailure = true) =>
        options
            .UseNpgsql(connectionString, npgsql =>
            {
                npgsql.UseVector();
                if (retryOnFailure)
                {
                    npgsql.EnableRetryOnFailure(5);
                }
            })
            .UseSnakeCaseNamingConvention();
}
