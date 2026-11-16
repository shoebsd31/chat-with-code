using ChatWithCode.Core.Options;
using ChatWithCode.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ChatWithCode.Worker;

/// <summary>Deletes conversations with no new messages for Chat:RetentionDays (default 30). Runs hourly.</summary>
public sealed class ConversationRetentionService(
    IServiceScopeFactory scopes,
    IOptions<ChatOptions> options,
    TimeProvider clock,
    ILogger<ConversationRetentionService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, clock);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DeleteExpiredAsync(stoppingToken);
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // Try again next hour.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                logger.LogError(ex, "Conversation clean-up failed; retrying in an hour");
                await Task.Delay(Interval, clock, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    internal async Task<int> DeleteExpiredAsync(CancellationToken cancellationToken)
    {
        var cutoff = clock.GetUtcNow() - TimeSpan.FromDays(options.Value.RetentionDays);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatWithCodeDbContext>();

        // Messages and citations go with their conversation (cascade delete).
        var deleted = await db.Conversations.Where(c => c.UpdatedAt < cutoff).ExecuteDeleteAsync(cancellationToken);
        if (deleted > 0)
        {
            logger.LogInformation("Deleted {Count} conversation(s) with no activity for {Days} days", deleted, options.Value.RetentionDays);
        }

        return deleted;
    }
}
