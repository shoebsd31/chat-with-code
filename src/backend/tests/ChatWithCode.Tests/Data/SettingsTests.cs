using ChatWithCode.Core.Abstractions;
using ChatWithCode.Core.Domain;
using ChatWithCode.Core.Options;
using ChatWithCode.Data;
using ChatWithCode.Data.Entities;
using ChatWithCode.Data.Settings;
using ChatWithCode.Tests.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ChatWithCode.Tests.Data;

public sealed class SettingsMergeTests
{
    private static readonly PlainSecretProtector Secrets = new();

    [Fact]
    public void Project_values_override_organization_defaults()
    {
        var org = Row(endpoint: "https://org.openai.azure.com/", embed: "embed-org", key: "enc:org-key", chat: "chat-org");
        var project = new ModelSettings { Scope = SettingScope.Project, ProjectId = Guid.NewGuid(), ChatDeployment = "chat-project", ApiKeyProtected = "enc:project-key" };

        var merged = ModelSettingsResolver.Merge(org, project, Secrets);

        Assert.Equal(new Uri("https://org.openai.azure.com/"), merged.AzureOpenAIEndpoint); // inherited
        Assert.Equal("embed-org", merged.EmbeddingDeployment);                               // inherited
        Assert.Equal("chat-project", merged.ChatDeployment);                                 // overridden
        Assert.Equal("project-key", merged.ApiKey);                                          // overridden and decrypted
        Assert.Equal(1536, merged.EmbeddingDimensions);
        Assert.Equal(VectorStoreKind.PgVector, merged.VectorStore);
    }

    [Fact]
    public void Missing_values_are_listed()
    {
        var error = Assert.Throws<SettingsIncompleteException>(() => ModelSettingsResolver.Merge(null, null, Secrets));

        Assert.Contains("Azure OpenAI endpoint", error.Message, StringComparison.Ordinal);
        Assert.Contains("embedding deployment", error.Message, StringComparison.Ordinal);
        Assert.Contains("API key", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Managed_identity_needs_no_key()
    {
        var org = Row(endpoint: "https://org.openai.azure.com/", embed: "embed", key: null);
        org.UseManagedIdentity = true;

        var merged = ModelSettingsResolver.Merge(org, null, Secrets);

        Assert.True(merged.UseManagedIdentity);
        Assert.Null(merged.ApiKey);
    }

    [Fact]
    public void Azure_AI_Search_requires_its_own_settings()
    {
        var org = Row(endpoint: "https://org.openai.azure.com/", embed: "embed", key: "enc:k");
        org.VectorStore = VectorStoreKind.AzureAISearch;

        var error = Assert.Throws<SettingsIncompleteException>(() => ModelSettingsResolver.Merge(org, null, Secrets));
        Assert.Contains("Azure AI Search endpoint", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_text_never_contains_keys()
    {
        var merged = ModelSettingsResolver.Merge(Row("https://org.openai.azure.com/", "embed", "enc:super-secret"), null, Secrets);
        Assert.DoesNotContain("super-secret", merged.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Data_protection_round_trips_and_explains_a_lost_key_ring()
    {
        var protector = new DataProtectionSecretProtector(new EphemeralDataProtectionProvider());
        var stored = protector.Protect("my-key");

        Assert.NotEqual("my-key", stored);
        Assert.Equal("my-key", protector.Unprotect(stored));

        var otherKeyRing = new DataProtectionSecretProtector(new EphemeralDataProtectionProvider());
        var error = Assert.Throws<InvalidOperationException>(() => otherKeyRing.Unprotect(stored));
        Assert.Contains("key ring", error.Message, StringComparison.Ordinal);
    }

    private static ModelSettings Row(string endpoint, string embed, string? key, string? chat = null) => new()
    {
        Scope = SettingScope.Organization,
        AzureOpenAIEndpoint = endpoint,
        EmbeddingDeployment = embed,
        ApiKeyProtected = key,
        ChatDeployment = chat,
    };
}

[Collection(PostgresCollection.Name)]
public sealed class SettingsBootstrapTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Configuration_seeds_organization_defaults_once()
    {
        await using var t = await postgres.CreateMigratedDbAsync();
        await new DatabaseInitializer(t.Context, t.Clock, NullLogger<DatabaseInitializer>.Instance).InitializeAsync("contoso");

        var config = new AzureOpenAIBootstrapOptions
        {
            Endpoint = "https://contoso.openai.azure.com/",
            ApiKey = "from-env",
            EmbeddingDeployment = "text-embedding-3-small",
            ChatDeployment = "gpt-6-luna",
        };
        Assert.True(await Bootstrapper(t, config).ApplyAsync());

        // A second start with different values doesn't overwrite what's saved.
        config.EmbeddingDeployment = "changed";
        Assert.False(await Bootstrapper(t, config).ApplyAsync());

        var row = await t.NewContext().ModelSettings.SingleAsync();
        Assert.Equal("text-embedding-3-small", row.EmbeddingDeployment);
        Assert.Equal("enc:from-env", row.ApiKeyProtected);
        Assert.Equal(SettingScope.Organization, row.Scope);
        Assert.Equal("configuration", row.UpdatedBy);
    }

    [Fact]
    public async Task Nothing_is_saved_without_an_endpoint()
    {
        await using var t = await postgres.CreateMigratedDbAsync();
        await new DatabaseInitializer(t.Context, t.Clock, NullLogger<DatabaseInitializer>.Instance).InitializeAsync("contoso");

        Assert.False(await Bootstrapper(t, new AzureOpenAIBootstrapOptions()).ApplyAsync());
        Assert.False(await t.NewContext().ModelSettings.AnyAsync());
    }

    private static ModelSettingsBootstrapper Bootstrapper(TestDb t, AzureOpenAIBootstrapOptions config) => new(
        t.NewContext(),
        new PlainSecretProtector(),
        t.Clock,
        Options.Create(new AzureDevOpsOptions { Organization = "contoso" }),
        Options.Create(config),
        Options.Create(new VectorStoreBootstrapOptions()),
        NullLogger<ModelSettingsBootstrapper>.Instance);
}
