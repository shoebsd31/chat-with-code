using ChatWithCode.Core.Abstractions;
using ChatWithCode.Core.Domain;
using ChatWithCode.Core.Options;
using ChatWithCode.Data;
using ChatWithCode.Data.Queue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ChatWithCode.Worker;

/// <summary>
/// Checks every indexed branch for new commits and queues re-indexing when the tip moved.
/// Works when Azure DevOps can't reach the backend with push notifications. With
/// Indexing:PollIntervalMinutes = 0 it checks once at startup; otherwise every N minutes.
/// </summary>
public sealed class BranchPoller(
    IServiceScopeFactory scopes,
    IOptions<IndexingOptions> options,
    TimeProvider clock,
    ILogger<BranchPoller> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        if (!o.PollingEnabled)
        {
            logger.LogInformation("Checking for new commits is turned off (Indexing:PollingEnabled = false)");
            return;
        }

        // Give the API a moment to finish migrations on a first start.
        await Task.Delay(TimeSpan.FromSeconds(10), clock, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        do
        {
            try
            {
                await CheckOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // Try again next round.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                logger.LogError(ex, "Checking for new commits failed");
            }

            if (o.PollIntervalMinutes == 0)
            {
                logger.LogInformation("Checked for new commits once (Indexing:PollIntervalMinutes = 0); set it above 0 to keep checking");
                break;
            }

            await Task.Delay(TimeSpan.FromMinutes(o.PollIntervalMinutes), clock, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        while (!stoppingToken.IsCancellationRequested);
    }

    /// <summary>One round over all indexed branches. Returns how many were queued.</summary>
    internal async Task<int> CheckOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatWithCodeDbContext>();
        var ado = scope.ServiceProvider.GetRequiredService<IAdoGitClient>();
        var queue = scope.ServiceProvider.GetRequiredService<IndexJobQueue>();

        var branches = await db.IndexedBranches.AsNoTracking()
            .Where(b => b.Repository!.IsEnabled)
            .Select(b => new
            {
                b.Id,
                b.BranchName,
                b.LastIndexedCommit,
                b.Repository!.AdoRepositoryId,
                RepositoryName = b.Repository.Name,
                Busy = db.IndexJobs.Any(j => j.IndexedBranchId == b.Id && (j.State == IndexJobState.Queued || j.State == IndexJobState.Running)),
            })
            .ToListAsync(cancellationToken);

        var queued = 0;
        foreach (var branch in branches.Where(b => !b.Busy))
        {
            string? head;
            try
            {
                head = await ado.GetBranchHeadAsync(branch.AdoRepositoryId, branch.BranchName, cancellationToken);
            }
            catch (Exception ex) when (ex is AdoAccessException or HttpRequestException)
            {
                logger.LogWarning("Couldn't check {Repository} {Branch} for new commits: {Message}", branch.RepositoryName, branch.BranchName, ex.Message);
                continue;
            }

            if (head is null)
            {
                await db.IndexedBranches.Where(b => b.Id == branch.Id).ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.Status, BranchIndexStatus.Failed)
                    .SetProperty(b => b.LastError, "This branch no longer exists in Azure DevOps. Remove it from the index in Project settings."), cancellationToken);
                continue;
            }

            if (head != branch.LastIndexedCommit)
            {
                await queue.EnqueueAsync(branch.Id, IndexJobType.Incremental, head, IndexJobTrigger.Poll, cancellationToken);
                queued++;
            }
        }

        if (queued > 0)
        {
            logger.LogInformation("Found new commits on {Count} branch(es); queued them for indexing", queued);
        }

        return queued;
    }
}
