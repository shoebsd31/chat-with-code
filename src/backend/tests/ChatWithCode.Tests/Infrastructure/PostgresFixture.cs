using ChatWithCode.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Testcontainers.PostgreSql;

namespace ChatWithCode.Tests.Infrastructure;

/// <summary>
/// One Postgres (with pgvector, same image as deploy/docker-compose.yml) for the whole test run.
/// Every test gets its own freshly migrated database, so tests never see each other's data.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("pgvector/pgvector:pg17")
        .WithDatabase("postgres")
        .Build();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>Create an empty database and return its connection string.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var name = $"test_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(_container.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE {name}", connection);
            await command.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name }.ConnectionString;
    }

    /// <summary>Create a migrated database and a context bound to it.</summary>
    public async Task<TestDb> CreateMigratedDbAsync()
    {
        var connectionString = await CreateDatabaseAsync();
        var db = TestDb.Create(connectionString);
        await db.Context.Database.MigrateAsync();
        return db;
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres";
}

/// <summary>A database context plus a controllable clock for one test.</summary>
public sealed class TestDb : IAsyncDisposable
{
    private TestDb(string connectionString, ChatWithCodeDbContext context)
    {
        ConnectionString = connectionString;
        Context = context;
    }

    public string ConnectionString { get; }
    public ChatWithCodeDbContext Context { get; }
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero));

    public static TestDb Create(string connectionString) => new(connectionString, NewContext(connectionString));

    /// <summary>A second, independent context on the same database (to simulate another worker).</summary>
    public ChatWithCodeDbContext NewContext() => NewContext(ConnectionString);

    private static ChatWithCodeDbContext NewContext(string connectionString) =>
        new((DbContextOptions<ChatWithCodeDbContext>)DataServiceCollectionExtensions
            .ConfigureDb(new DbContextOptionsBuilder<ChatWithCodeDbContext>(), connectionString, retryOnFailure: false)
            .Options);

    public ValueTask DisposeAsync() => Context.DisposeAsync();
}
