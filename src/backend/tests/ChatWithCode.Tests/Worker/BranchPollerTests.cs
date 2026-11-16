using ChatWithCode.Core.Abstractions;
using ChatWithCode.Core.Domain;
using ChatWithCode.Core.Options;
using ChatWithCode.Data;
using ChatWithCode.Data.Entities;
using ChatWithCode.Data.Queue;
using ChatWithCode.Tests.Infrastructure;
using ChatWithCode.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ChatWithCode.Tests.Worker;

[Collection(PostgresCollection.Name)]
public sealed class BranchPollerTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Queues_branches_whose_tip_moved()
    {
        await using var t = await postgres.CreateMigratedDbAsync();
        var ado = new FakeAdoGitClient();
        var indexed = ado.Commit(new() { ["a.txt"] = "a" });
        var (main, _) = await SeedAsync(t, ado, lastIndexedCommit: indexed);
        var head = ado.Commit(new() { ["a.txt"] = "b" });

        Assert.Equal(1, await Poller(t, ado).CheckOnceAsync(CancellationToken.None));

        var job = await t.NewContext().IndexJobs.SingleAsync();
        Assert.Equal((main.Id, IndexJobType.Incremental, head), (job.IndexedBranchId, job.Type, job.TargetCommit));
    }

    [Fact]
    public async Task Leaves_up_to_date_and_busy_branches_alone()
    {
        await using var t = await postgres.CreateMigratedDbAsync();
        var ado = new FakeAdoGitClient();
        var head = ado.Commit(new() { ["a.txt"] = "a" });
        var (main, _) = await SeedAsync(t, ado, lastIndexedCommit: head);

        Assert.Equal(0, await Poller(t, ado).CheckOnceAsync(CancellationToken.None)); // up to date

        ado.Commit(new() { ["a.txt"] = "b" });
        await new IndexJobQueue(t.NewContext(), t.Clock).EnqueueAsync(main.Id, IndexJobType.Full);
        Assert.Equal(0, await Poller(t, ado).CheckOnceAsync(CancellationToken.None)); // a job is already waiting
        Assert.Equal(1, await t.NewContext().IndexJobs.CountAsync());
    }

    [Fact]
    public async Task Marks_branches_that_were_deleted_in_Azure_DevOps()
    {
        await using var t = await postgres.CreateMigratedDbAsync();
        var ado = new FakeAdoGitClient();
        ado.Commit(new() { ["a.txt"] = "a" });
        var (_, gone) = await SeedAsync(t, ado, lastIndexedCommit: "x", extraBranch: "refs/heads/old-feature");

        await Poller(t, ado).CheckOnceAsync(CancellationToken.None);

        var branch = await t.NewContext().IndexedBranches.SingleAsync(b => b.Id == gone!.Id);
        Assert.Equal(BranchIndexStatus.Failed, branch.Status);
        Assert.Contains("no longer exists", branch.LastError, StringComparison.Ordinal);
    }

    private static async Task<(IndexedBranch Main, IndexedBranch? Extra)> SeedAsync(TestDb t, FakeAdoGitClient ado, string lastIndexedCommit, string? extraBranch = null)
    {
        var org = new Organization { Id = Guid.NewGuid(), Name = "contoso", CreatedAt = DateTimeOffset.UtcNow };
        var repo = new Repository
        {
            Id = Guid.NewGuid(), OrganizationId = org.Id, AdoRepositoryId = ado.Repository.Id, AdoProjectId = ado.Repository.ProjectId,
            ProjectName = "Fabrikam", Name = "web-app", CreatedAt = DateTimeOffset.UtcNow,
        };
        var main = new IndexedBranch { Id = Guid.NewGuid(), RepositoryId = repo.Id, BranchName = "refs/heads/main", Status = BranchIndexStatus.Ready, LastIndexedCommit = lastIndexedCommit };
        var extra = extraBranch is null ? null : new IndexedBranch { Id = Guid.NewGuid(), RepositoryId = repo.Id, BranchName = extraBranch, Status = BranchIndexStatus.Ready, LastIndexedCommit = "y" };
        t.Context.AddRange(org, repo, main);
        if (extra is not null)
        {
            t.Context.Add(extra);
        }

        await t.Context.SaveChangesAsync();
        return (main, extra);
    }

    private static BranchPoller Poller(TestDb t, IAdoGitClient ado)
    {
        var services = new ServiceCollection()
            .AddDbContext<ChatWithCodeDbContext>(o => DataServiceCollectionExtensions.ConfigureDb(o, t.ConnectionString, retryOnFailure: false))
            .AddSingleton<TimeProvider>(t.Clock)
            .AddScoped<IndexJobQueue>()
            .AddSingleton(ado)
            .BuildServiceProvider();
        return new BranchPoller(services.GetRequiredService<IServiceScopeFactory>(), Options.Create(new IndexingOptions()), t.Clock, NullLogger<BranchPoller>.Instance);
    }
}
