using ChatWithCode.Data;
using ChatWithCode.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ChatWithCode.Tests.Data;

/// <summary>
/// In Azure the database password is an Entra access token supplied by a periodic password
/// provider. This checks that wiring end to end, with a provider that returns the test password.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TokenSignInTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Connection_string_without_password_works_with_a_password_provider()
    {
        var full = new NpgsqlConnectionStringBuilder(await postgres.CreateDatabaseAsync());
        var password = full.Password!;
        full.Password = null;
        var calls = 0;

        var options = (DbContextOptions<ChatWithCodeDbContext>)DataServiceCollectionExtensions.ConfigureDb(
            new DbContextOptionsBuilder<ChatWithCodeDbContext>(), full.ConnectionString, retryOnFailure: false,
            configureDataSource: ds => ds.UsePeriodicPasswordProvider((_, _) => { calls++; return ValueTask.FromResult(password); }, TimeSpan.FromMinutes(50), TimeSpan.FromSeconds(5))).Options;
        await using var db = new ChatWithCodeDbContext(options);

        await db.Database.MigrateAsync(); // includes CREATE EXTENSION vector, so the vector plugin is active too
        Assert.Equal(0, await db.CodeChunks.CountAsync());
        Assert.True(calls >= 1);
    }
}
