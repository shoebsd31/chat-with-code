using ChatWithCode.Core.Domain;
using ChatWithCode.Data.Queue;
using ChatWithCode.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ChatWithCode.Tests.Data;

[Collection(PostgresCollection.Name)]
public sealed class IndexJobQueueTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Enqueue_marks_branch_queued_and_reuses_a_waiting_job()
    {
        await using var t = await postgres.CreateMigratedDbAsync();
        var (_, _, main) = await Seed.RepoWithMainAsync(t.Context);
        var queue = new IndexJobQueue(t.Context, t.Clock);

        var first = await queue.EnqueueAsync(main.Id, IndexJobType.Incremental, "aaa");
        var second = await queue.EnqueueAsync(main.Id, IndexJobType.Full, "bbb");

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, await t.Context.IndexJobs.CountAsync());
        var job = await t.NewContext().IndexJobs.SingleAsync();
        Assert.Equal(IndexJobType.Full, job.Type); // a full re-index wins over an incremental one
        Assert.Equal("bbb", job.TargetCommit);
        Assert.Equal(BranchIndexStatus.Queued, (await t.NewContext().IndexedBranches.SingleAsync()).Status);
    }

    [Theory]
    [InlineData(IndexJobTrigger.Poll, IndexJobTrigger.Push, IndexJobTrigger.Push)]
    [InlineData(IndexJobTrigger.Poll, IndexJobTrigger.Admin, IndexJobTrigger.Admin)]
    [InlineData(IndexJobTrigger.Admin, IndexJobTrigger.Poll, IndexJobTrigger.Admin)]
    [InlineData(IndexJobTrigger.Push, IndexJobTrigger.Poll, IndexJobTrigger.Push)]
    public async Task A_waiting_job_keeps_the_trigger_that_allows_re_splitting(IndexJobTrigger first, IndexJobTrigger second, IndexJobTrigger expected)
    {
        await using var t = await postgres.CreateMigratedDbAsync();
        var (_, _, main) = await Seed.RepoWithMainAsync(t.Context);
        var queue = new IndexJobQueue(t.Context, t.Clock);

        await queue.EnqueueAsync(main.Id, IndexJobType.Incremental, "aaa", first);
        await queue.EnqueueAsync(main.Id, IndexJobType.Incremental, "bbb", second);

        Assert.Equal(expected, (await t.NewContext().IndexJobs.SingleAsync()).Trigger);
    }

    [Fact]
    public async Task Claim_returns_null_when_queue_is_empty()
    {
        await using var t = await postgres.CreateMigratedDbAsync();
        var queue = new IndexJobQueue(t.Context, t.Clock);

        Assert.Null(await queue.TryClaimAsync("worker-1"));
    }

    [Fact]
    public async Task Claim_takes_oldest_job_and_marks_it_running()
    {
        await using var t = await postgres.CreateMigratedDbAsync();
        var (_, repo, main) = await Seed.RepoWithMainAsync(t.Context);
        var dev = await Seed.AddBranchAsync(t.Context, repo.Id, "refs/heads/dev");
        var queue = new IndexJobQueue(t.Context, t.Clock);

        var older = await queue.EnqueueAsync(main.Id, IndexJobType.Full);
        t.Clock.Advance(TimeSpan.FromSeconds(1));
        await queue.EnqueueAsync(dev.Id, IndexJobType.Full);

        var claimed = await queue.TryClaimAsync("worker-1");

        Assert.NotNull(claimed);
        Assert.Equal(older.Id, claimed.Id);
        Assert.Equal(IndexJobState.Running, claimed.State);
        Assert.Equal(1, claimed.Attempts);
        Assert.Equal("worker-1", claimed.LockedBy);
        var branch = await t.NewContext().IndexedBranches.SingleAsync(b => b.Id == main.Id);
        Assert.Equal(BranchIndexStatus.Indexing, branch.Status);
    }

    [Fact]
    public async Task Concurrent_workers_never_claim_the_same_job()
    {
        await using var t = await postgres.CreateMigratedDbAsync();
        var (_, repo, _) = await Seed.RepoWithMainAsync(t.Context);
        var setup = new IndexJobQueue(t.Context, t.Clock);
        for (var i = 0; i < 10; i++)
        {
            var branch = await Seed.AddBranchAsync(t.Context, repo.Id, $"refs/heads/feature-{i}");
            await setup.EnqueueAsync(branch.Id, IndexJobType.Full);
        }

        // Five "workers", each with its own connection, race for the ten jobs.
        var claims = await Task.WhenAll(Enumerable.Range(0, 5).Select(async w =>
        {
            await using var db = t.NewContext();
            var queue = new IndexJobQueue(db, t.Clock);
            var mine = new List<Guid>();
            while (await queue.TryClaimAsync($"worker-{w}") is { } job)
            {
                mine.Add(job.Id);
            }

            return mine;
        }));

        var all = claims.SelectMany(c => c).ToList();
        Assert.Equal(10, all.Count);
        Assert.Equal(10, all.Distinct().Count());
    }

    [Fact]
    public async Task Failed_job_is_retried_with_backoff_then_marked_failed()
    {
        await using var t = await postgres.CreateMigratedDbAsync();
        var (_, _, main) = await Seed.RepoWithMainAsync(t.Context);
        var queue = new IndexJobQueue(t.Context, t.Clock);
        var job = await queue.EnqueueAsync(main.Id, IndexJobType.Full);

        // Attempt 1 fails: retried after 1 minute, not before.
        await queue.TryClaimAsync("w");
        await queue.FailAsync(job.Id, "Azure OpenAI returned 429");
        Assert.Null(await queue.TryClaimAsync("w"));
        t.Clock.Advance(TimeSpan.FromMinutes(1));

        // Attempt 2 fails: retried after 4 minutes.
        Assert.NotNull(await queue.TryClaimAsync("w"));
        await queue.FailAsync(job.Id, "Azure OpenAI returned 429");
        t.Clock.Advance(TimeSpan.FromMinutes(4));

        // Attempt 3 fails: out of attempts.
        Assert.NotNull(await queue.TryClaimAsync("w"));
        await queue.FailAsync(job.Id, "Azure OpenAI returned 429");

        await using var check = t.NewContext();
        var stored = await check.IndexJobs.SingleAsync();
        Assert.Equal(IndexJobState.Failed, stored.State);
        Assert.Equal(3, stored.Attempts);
        Assert.NotNull(stored.FinishedAt);
        var branch = await check.IndexedBranches.SingleAsync();
        Assert.Equal(BranchIndexStatus.Failed, branch.Status);
        Assert.Equal("Azure OpenAI returned 429", branch.LastError);
    }

    [Fact]
    public async Task Complete_marks_job_succeeded()
    {
        await using var t = await postgres.CreateMigratedDbAsync();
        var (_, _, main) = await Seed.RepoWithMainAsync(t.Context);
        var queue = new IndexJobQueue(t.Context, t.Clock);
        var job = await queue.EnqueueAsync(main.Id, IndexJobType.Incremental);
        await queue.TryClaimAsync("w");

        await queue.CompleteAsync(job.Id);

        var stored = await t.NewContext().IndexJobs.SingleAsync();
        Assert.Equal(IndexJobState.Succeeded, stored.State);
        Assert.Null(stored.LockedBy);
    }

    [Fact]
    public async Task Stale_running_jobs_are_requeued()
    {
        await using var t = await postgres.CreateMigratedDbAsync();
        var (_, _, main) = await Seed.RepoWithMainAsync(t.Context);
        var queue = new IndexJobQueue(t.Context, t.Clock);
        await queue.EnqueueAsync(main.Id, IndexJobType.Full);
        await queue.TryClaimAsync("crashed-worker");

        t.Clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(0, await queue.RequeueStaleAsync(TimeSpan.FromMinutes(60)));

        t.Clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(1, await queue.RequeueStaleAsync(TimeSpan.FromMinutes(60)));
        Assert.NotNull(await queue.TryClaimAsync("new-worker"));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 4)]
    [InlineData(3, 16)]
    public void Retry_delay_grows_exponentially(int attempt, int expectedMinutes) =>
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), IndexJobQueue.RetryDelay(attempt));
}
