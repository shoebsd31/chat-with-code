using ChatWithCode.Core.Domain;
using ChatWithCode.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ChatWithCode.Data.Queue;

/// <summary>
/// Job queue on top of the index_jobs table. Workers claim jobs with
/// SELECT … FOR UPDATE SKIP LOCKED, so several workers never take the same job
/// and self-hosting needs no separate queue service.
/// </summary>
public sealed class IndexJobQueue(ChatWithCodeDbContext db, TimeProvider clock)
{
    /// <summary>
    /// Queue work for a branch. If a job for the branch is already waiting, that job is reused
    /// (a full re-index wins over an incremental one) instead of piling up duplicates.
    /// </summary>
    public async Task<IndexJob> EnqueueAsync(Guid indexedBranchId, IndexJobType type, string? targetCommit = null, CancellationToken cancellationToken = default)
    {
        var waiting = await db.IndexJobs
            .AsNoTracking()
            .Where(j => j.IndexedBranchId == indexedBranchId && j.State == IndexJobState.Queued)
            .OrderBy(j => j.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (waiting is not null)
        {
            waiting.Type = type == IndexJobType.Full ? IndexJobType.Full : waiting.Type;
            waiting.TargetCommit = targetCommit ?? waiting.TargetCommit;
            await db.IndexJobs
                .Where(j => j.Id == waiting.Id && j.State == IndexJobState.Queued)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Type, waiting.Type)
                    .SetProperty(j => j.TargetCommit, waiting.TargetCommit), cancellationToken);
            return waiting;
        }

        var now = clock.GetUtcNow();
        var job = new IndexJob
        {
            Id = Guid.CreateVersion7(),
            IndexedBranchId = indexedBranchId,
            Type = type,
            TargetCommit = targetCommit,
            CreatedAt = now,
            AvailableAt = now,
        };
        db.IndexJobs.Add(job);

        var branch = await db.IndexedBranches.FindAsync([indexedBranchId], cancellationToken)
            ?? throw new InvalidOperationException($"Indexed branch {indexedBranchId} does not exist.");
        if (branch.Status != BranchIndexStatus.Indexing)
        {
            branch.Status = BranchIndexStatus.Queued;
        }

        await db.SaveChangesAsync(cancellationToken);
        return job;
    }

    /// <summary>Atomically take the oldest due job, or null when nothing is waiting.</summary>
    public async Task<IndexJob?> TryClaimAsync(string workerId, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var queued = nameof(IndexJobState.Queued);
        var running = nameof(IndexJobState.Running);

        var claimed = await db.IndexJobs
            .FromSql($"""
                UPDATE index_jobs
                SET state = {running}, attempts = attempts + 1, started_at = {now}, locked_by = {workerId}
                WHERE id = (
                    SELECT id FROM index_jobs
                    WHERE state = {queued} AND available_at <= {now}
                    ORDER BY created_at
                    FOR UPDATE SKIP LOCKED
                    LIMIT 1)
                RETURNING *
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var job = claimed.SingleOrDefault();
        if (job is not null)
        {
            await db.IndexedBranches
                .Where(b => b.Id == job.IndexedBranchId)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, BranchIndexStatus.Indexing), cancellationToken);
        }

        return job;
    }

    public async Task CompleteAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        await db.IndexJobs
            .Where(j => j.Id == jobId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.State, IndexJobState.Succeeded)
                .SetProperty(j => j.FinishedAt, now)
                .SetProperty(j => j.Error, (string?)null)
                .SetProperty(j => j.LockedBy, (string?)null), cancellationToken);
    }

    /// <summary>
    /// Record a failed attempt. The job is retried with exponential backoff (1, 4, 16 minutes…)
    /// until it runs out of attempts; then it is marked failed and the branch shows the error.
    /// </summary>
    public async Task FailAsync(Guid jobId, string error, CancellationToken cancellationToken = default)
    {
        // Read the row fresh: the claim ran as a raw UPDATE, so any tracked copy is out of date.
        var job = await db.IndexJobs.AsNoTracking().SingleAsync(j => j.Id == jobId, cancellationToken);
        var now = clock.GetUtcNow();
        var message = Truncate(error, 4000);

        if (job.Attempts < job.MaxAttempts)
        {
            var retryAt = now + RetryDelay(job.Attempts);
            await db.IndexJobs
                .Where(j => j.Id == jobId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.State, IndexJobState.Queued)
                    .SetProperty(j => j.AvailableAt, retryAt)
                    .SetProperty(j => j.Error, message)
                    .SetProperty(j => j.LockedBy, (string?)null), cancellationToken);
            return;
        }

        await db.IndexJobs
            .Where(j => j.Id == jobId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.State, IndexJobState.Failed)
                .SetProperty(j => j.FinishedAt, now)
                .SetProperty(j => j.Error, message)
                .SetProperty(j => j.LockedBy, (string?)null), cancellationToken);
        await db.IndexedBranches
            .Where(b => b.Id == job.IndexedBranchId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, BranchIndexStatus.Failed)
                .SetProperty(b => b.LastError, message), cancellationToken);
    }

    /// <summary>Put back jobs left "running" by a worker that crashed or was stopped mid-job.</summary>
    public async Task<int> RequeueStaleAsync(TimeSpan olderThan, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var cutoff = now - olderThan;
        return await db.IndexJobs
            .Where(j => j.State == IndexJobState.Running && j.StartedAt < cutoff)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.State, IndexJobState.Queued)
                .SetProperty(j => j.LockedBy, (string?)null)
                .SetProperty(j => j.AvailableAt, now), cancellationToken);
    }

    public static TimeSpan RetryDelay(int attempt) => TimeSpan.FromMinutes(Math.Pow(4, Math.Max(0, attempt - 1)));

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
