using ChatWithCode.Core.Abstractions;
using ChatWithCode.Core.Options;
using ChatWithCode.Data.Queue;
using Microsoft.Extensions.Options;

namespace ChatWithCode.Worker;

/// <summary>Claims index jobs from the queue one at a time and runs them.</summary>
public sealed class IndexJobWorker(
    IServiceScopeFactory scopes,
    IOptions<IndexingOptions> options,
    ILogger<IndexJobWorker> logger) : BackgroundService
{
    private readonly string _workerId = $"{Environment.MachineName}:{Environment.ProcessId}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var idleDelay = TimeSpan.FromSeconds(options.Value.QueuePollSeconds);
        logger.LogInformation("Index worker {WorkerId} started", _workerId);

        await RequeueStaleJobsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            bool didWork;
            try
            {
                didWork = await RunNextJobAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // The loop must survive any error (for example, the database restarting).
            catch (Exception ex)
#pragma warning restore CA1031
            {
                logger.LogError(ex, "Index worker loop error; retrying shortly");
                didWork = false;
            }

            if (!didWork)
            {
                await Task.Delay(idleDelay, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }

        logger.LogInformation("Index worker {WorkerId} stopped", _workerId);
    }

    /// <summary>Runs one job if one is due. Returns false when the queue is empty.</summary>
    internal async Task<bool> RunNextJobAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var queue = scope.ServiceProvider.GetRequiredService<IndexJobQueue>();

        var job = await queue.TryClaimAsync(_workerId, cancellationToken);
        if (job is null)
        {
            return false;
        }

        logger.LogInformation("Starting {Type} index job {JobId} (attempt {Attempt})", job.Type, job.Id, job.Attempts);
        try
        {
            var handler = scope.ServiceProvider.GetRequiredService<IIndexJobHandler>();
            await handler.HandleAsync(new IndexJobContext(job.Id, job.IndexedBranchId, job.Type, job.Attempts, job.TargetCommit, job.Trigger), cancellationToken);
            await queue.CompleteAsync(job.Id, CancellationToken.None);
            logger.LogInformation("Finished index job {JobId}", job.Id);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down: leave the job "running"; it is re-queued as stale on the next start.
            throw;
        }
#pragma warning disable CA1031 // Any handler failure is recorded on the job and retried.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            logger.LogWarning(ex, "Index job {JobId} failed", job.Id);
            await queue.FailAsync(job.Id, ex.Message, CancellationToken.None);
        }

        return true;
    }

    private async Task RequeueStaleJobsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var queue = scope.ServiceProvider.GetRequiredService<IndexJobQueue>();
            var count = await queue.RequeueStaleAsync(TimeSpan.FromMinutes(options.Value.StaleJobMinutes), cancellationToken);
            if (count > 0)
            {
                logger.LogWarning("Re-queued {Count} index job(s) left running by a stopped worker", count);
            }
        }
#pragma warning disable CA1031 // Not fatal: the main loop retries database access.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            logger.LogError(ex, "Could not re-queue stale index jobs");
        }
    }
}
