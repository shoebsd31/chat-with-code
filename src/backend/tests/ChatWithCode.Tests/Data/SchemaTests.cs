using ChatWithCode.Core.Domain;
using ChatWithCode.Data;
using ChatWithCode.Data.Entities;
using ChatWithCode.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChatWithCode.Tests.Data;

[Collection(PostgresCollection.Name)]
public sealed class SchemaTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Initializer_applies_migrations_and_registers_organization_once()
    {
        await using var t = TestDb.Create(await postgres.CreateDatabaseAsync());
        var initializer = new DatabaseInitializer(t.Context, t.Clock, NullLogger<DatabaseInitializer>.Instance);

        await initializer.InitializeAsync("contoso");
        await initializer.InitializeAsync("contoso"); // second start is a no-op

        Assert.Empty(await t.Context.Database.GetPendingMigrationsAsync());
        Assert.Equal("contoso", (await t.NewContext().Organizations.SingleAsync()).Name);
    }

    [Fact]
    public async Task Only_one_organization_level_settings_row_is_allowed()
    {
        await using var t = await postgres.CreateMigratedDbAsync();
        var (org, _, _) = await Seed.RepoWithMainAsync(t.Context);

        t.Context.ModelSettings.Add(OrgSettings(org.Id));
        await t.Context.SaveChangesAsync();

        await using var other = t.NewContext();
        other.ModelSettings.Add(OrgSettings(org.Id));
        await Assert.ThrowsAsync<DbUpdateException>(() => other.SaveChangesAsync());
    }

    [Fact]
    public async Task Project_override_must_name_a_project()
    {
        await using var t = await postgres.CreateMigratedDbAsync();
        var (org, _, _) = await Seed.RepoWithMainAsync(t.Context);

        var invalid = OrgSettings(org.Id);
        invalid.Scope = SettingScope.Project; // but ProjectId is null
        t.Context.ModelSettings.Add(invalid);

        await Assert.ThrowsAsync<DbUpdateException>(() => t.Context.SaveChangesAsync());
    }

    [Fact]
    public async Task Conversation_targets_round_trip_as_json()
    {
        await using var t = await postgres.CreateMigratedDbAsync();
        var (org, repo, _) = await Seed.RepoWithMainAsync(t.Context);

        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            OrganizationId = org.Id,
            UserId = "user-1",
            Title = "How does login work?",
            Targets =
            [
                new ConversationTarget { RepositoryId = repo.Id, Branch = "refs/heads/main" },
                new ConversationTarget { RepositoryId = repo.Id, Branch = "refs/heads/dev" },
            ],
            CreatedAt = t.Clock.GetUtcNow(),
            UpdatedAt = t.Clock.GetUtcNow(),
        };
        t.Context.Conversations.Add(conversation);
        await t.Context.SaveChangesAsync();

        var loaded = await t.NewContext().Conversations.SingleAsync();
        Assert.Equal(["refs/heads/main", "refs/heads/dev"], loaded.Targets.Select(x => x.Branch));
    }

    private static ModelSettings OrgSettings(Guid organizationId) => new()
    {
        Id = Guid.NewGuid(),
        OrganizationId = organizationId,
        Scope = SettingScope.Organization,
        Provider = ModelProviderKind.AzureOpenAI,
        VectorStore = VectorStoreKind.PgVector,
        UpdatedAt = DateTimeOffset.UtcNow,
    };
}
