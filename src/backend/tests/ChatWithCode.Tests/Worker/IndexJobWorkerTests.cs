using ChatWithCode.Core.Abstractions;
using ChatWithCode.Core.Domain;
using ChatWithCode.Core.Options;
using ChatWithCode.Data;
using ChatWithCode.Data.Queue;
using ChatWithCode.Tests.Infrastructure;
using ChatWithCode.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChatWithCode.Tests.Worker;

[Collection(PostgresCollection.Name)]
public sealed class IndexJobWorkerTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Successful_handler_completes_the_job()
    {
        await using var t = await postgres.CreateMigratedDbAsync();
        var (_, _, main) = await Seed.RepoWithMainAsync(t.Context);
        await new IndexJobQueue(t.Context, t.Clock).EnqueueAsync(main.Id, IndexJobType.Full);
        var handler = new RecordingHandler();

        var ran = await CreateWorker(t, handler).RunNextJobAsync(CancellationToken.None);

        Assert.True(ran);
        Assert.Equal(main.Id, Assert.Single(handler.Seen).IndexedBranchId);
        Assert.Equal(IndexJobState.Succeeded, (await t.NewContext().IndexJobs.SingleAsync()).State);
    }

    [Fact]
    public async Task Failing_handler_records_error_and_requeues()
    {
        await using var t = await postgres.CreateMigratedDbAsync();
        var (_, _, main) = await Seed.RepoWithMainAsync(t.Context);
        await new IndexJobQueue(t.Context, t.Clock).EnqueueAsync(main.Id, IndexJobType.Full);
        var handler = new RecordingHandler { FailWith = "Repository not found" };

        await CreateWorker(t, handler).RunNextJobAsync(CancellationToken.None);

        var job = await t.NewContext().IndexJobs.SingleAsync();
        Assert.Equal(IndexJobState.Queued, job.State);
        Assert.Equal("Repository not found", job.Error);
    }

    [Fact]
    public async Task Empty_queue_reports_no_work()
    {
        await using var t = await postgres.CreateMigratedDbAsync();

        Assert.False(await CreateWorker(t, new RecordingHandler()).RunNextJobAsync(CancellationToken.None));
    }

    private static IndexJobWorker CreateWorker(TestDb t, IIndexJobHandler handler)
    {
        var services = new ServiceCollection();
        services.AddDbContext<ChatWithCodeDbContext>(o => DataServiceCollectionExtensions.ConfigureDb(o, t.ConnectionString, retryOnFailure: false));
        services.AddSingleton<TimeProvider>(t.Clock);
        services.AddScoped<IndexJobQueue>();
        services.AddSingleton(handler);
        var provider = services.BuildServiceProvider();

        return new IndexJobWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Options.Options.Create(new IndexingOptions()),
            NullLogger<IndexJobWorker>.Instance);
    }

    private sealed class RecordingHandler : IIndexJobHandler
    {
        public List<IndexJobContext> Seen { get; } = [];
        public string? FailWith { get; init; }

        public Task HandleAsync(IndexJobContext job, CancellationToken cancellationToken)
        {
            Seen.Add(job);
            return FailWith is null ? Task.CompletedTask : throw new InvalidOperationException(FailWith);
        }
    }
}
