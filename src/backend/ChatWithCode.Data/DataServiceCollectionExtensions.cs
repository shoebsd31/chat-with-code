using Azure.Core;
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

        // In Azure, sign in to PostgreSQL with the managed identity (Entra) instead of a password:
        // the access token is the password, and it's renewed before it expires.
        var useManagedIdentity = configuration.GetValue("Database:UseManagedIdentity", false);
        var credential = useManagedIdentity ? ManagedIdentity.Credential(configuration) : null;
        services.AddDbContext<ChatWithCodeDbContext>(o => ConfigureDb(o, connectionString, configureDataSource: credential is null ? null : ds =>
            ds.UsePeriodicPasswordProvider(
                async (_, ct) => (await credential.GetTokenAsync(new TokenRequestContext([PostgresScope]), ct)).Token,
                TimeSpan.FromMinutes(50),
                TimeSpan.FromSeconds(10))));
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IndexJobQueue>();
        services.AddScoped<DatabaseInitializer>();

        // The API and the worker share one key ring (a mounted volume), so either can decrypt API keys.
        // In Azure the key ring lives in a blob and is encrypted with a Key Vault key, so every replica shares it.
        var security = configuration.GetSection(SecurityOptions.SectionName).Get<SecurityOptions>() ?? new SecurityOptions();
        var dataProtection = services.AddDataProtection().SetApplicationName("ChatWithCode");
        if (security.DataProtectionBlobUri is { } blobUri)
        {
            var azure = ManagedIdentity.Credential(configuration);
            dataProtection.PersistKeysToAzureBlobStorage(blobUri, azure);
            if (security.DataProtectionKeyVaultKeyId is { } keyId)
            {
                dataProtection.ProtectKeysWithAzureKeyVault(keyId, azure);
            }
        }
        else
        {
            dataProtection.PersistKeysToFileSystem(new DirectoryInfo(security.DataProtectionKeysPath));
        }
        services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();

        services.AddOptions<AzureOpenAIBootstrapOptions>().Bind(configuration.GetSection(AzureOpenAIBootstrapOptions.SectionName));
        services.AddOptions<VectorStoreBootstrapOptions>().Bind(configuration.GetSection(VectorStoreBootstrapOptions.SectionName));
        services.AddScoped<IModelSettingsResolver, ModelSettingsResolver>();
        services.AddScoped<ModelSettingsBootstrapper>();
        return services;
    }

    /// <summary>Entra token scope for Azure Database for PostgreSQL.</summary>
    public const string PostgresScope = "https://ossrdbms-aad.database.windows.net/.default";

    /// <summary>Shared database configuration (also used by tests and the design-time factory).</summary>
    public static DbContextOptionsBuilder ConfigureDb(
        DbContextOptionsBuilder options,
        string connectionString,
        bool retryOnFailure = true,
        Action<Npgsql.NpgsqlDataSourceBuilder>? configureDataSource = null) =>
        options
            .UseNpgsql(connectionString, npgsql =>
            {
                npgsql.UseVector();
                if (configureDataSource is not null)
                {
                    npgsql.ConfigureDataSource(configureDataSource);
                }

                if (retryOnFailure)
                {
                    npgsql.EnableRetryOnFailure(5);
                }
            })
            .UseSnakeCaseNamingConvention();
}
