using ChatWithCode.Core.Abstractions;
using ChatWithCode.Core.Domain;
using ChatWithCode.Data;
using ChatWithCode.Worker.Indexing;
using Microsoft.EntityFrameworkCore;

namespace ChatWithCode.Worker;

/// <summary>
/// Once at startup, writes starter questions for indexed branches that have none yet (for example
/// branches indexed before starter questions existed). Uses the last indexed commit and doesn't
/// touch the index itself. Later changes are handled after each indexing run.
/// </summary>
public sealed class SuggestionBackfillService(IServiceScopeFactory scopes, ILogger<SuggestionBackfillService> logger) : BackgroundService
{
    internal static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartDelay, stoppingToken);
            await RunOnceAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
#pragma warning disable CA1031 // A failed backfill must not stop the worker; the next indexing run tries again.
        catch (Exception e)
#pragma warning restore CA1031
        {
            logger.LogWarning(e, "Couldn't write missing starter questions; they'll be written after the next indexing run");
        }
    }

    internal async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatWithCodeDbContext>();
        var missing = await db.IndexedBranches.AsNoTracking()
            .Where(b => b.Status == BranchIndexStatus.Ready && b.LastIndexedCommit != null && b.SuggestedQuestions == null && b.Repository!.IsEnabled)
            .Select(b => new { b.Id, b.LastIndexedCommit, b.Repository!.AdoRepositoryId })
            .ToListAsync(cancellationToken);

        var written = 0;
        foreach (var branch in missing)
        {
            await using var branchScope = scopes.CreateAsyncScope();
            var ado = branchScope.ServiceProvider.GetRequiredService<IAdoGitClient>();
            var suggester = branchScope.ServiceProvider.GetRequiredService<QuestionSuggester>();
            try
            {
                var tree = await ado.GetFilesAsync(branch.AdoRepositoryId, branch.LastIndexedCommit!, cancellationToken);
                written += await suggester.EnsureAsync(branch.Id, tree, cancellationToken) ? 1 : 0;
            }
            catch (AdoAccessException e)
            {
                logger.LogWarning("Couldn't read branch {BranchId} for starter questions: {Message}", branch.Id, e.Message);
            }
        }

        if (missing.Count > 0)
        {
            logger.LogInformation("Wrote starter questions for {Written} of {Missing} branches that had none", written, missing.Count);
        }

        return written;
    }
}
