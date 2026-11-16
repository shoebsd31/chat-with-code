using ChatWithCode.Core.Domain;
using ChatWithCode.Core.Options;
using ChatWithCode.Data;
using ChatWithCode.Data.Entities;
using ChatWithCode.Tests.Infrastructure;
using ChatWithCode.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ChatWithCode.Tests.Worker;

[Collection(PostgresCollection.Name)]
public sealed class ConversationRetentionTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Conversations_idle_longer_than_retention_are_deleted_with_their_messages()
    {
        await using var t = await postgres.CreateMigratedDbAsync();
        var (org, _, _) = await Seed.RepoWithMainAsync(t.Context);
        var now = t.Clock.GetUtcNow();
        t.Context.Conversations.AddRange(Conversation(org.Id, "old", now.AddDays(-31)), Conversation(org.Id, "recent", now.AddDays(-29)));
        await t.Context.SaveChangesAsync();

        var services = new ServiceCollection()
            .AddDbContext<ChatWithCodeDbContext>(o => DataServiceCollectionExtensions.ConfigureDb(o, t.ConnectionString, retryOnFailure: false))
            .BuildServiceProvider();
        var service = new ConversationRetentionService(
            services.GetRequiredService<IServiceScopeFactory>(), Options.Create(new ChatOptions { RetentionDays = 30 }), t.Clock,
            NullLogger<ConversationRetentionService>.Instance);

        Assert.Equal(1, await service.DeleteExpiredAsync(CancellationToken.None));

        await using var check = t.NewContext();
        Assert.Equal("recent", (await check.Conversations.SingleAsync()).Title);
        Assert.Equal(1, await check.Messages.CountAsync()); // the old conversation's message went with it
    }

    private static Conversation Conversation(Guid orgId, string title, DateTimeOffset updated) => new()
    {
        Id = Guid.NewGuid(),
        OrganizationId = orgId,
        UserId = "user-1",
        Title = title,
        CreatedAt = updated,
        UpdatedAt = updated,
        Messages = [new Message { Id = Guid.NewGuid(), Role = MessageRole.User, Content = title, CreatedAt = updated }],
    };
}
