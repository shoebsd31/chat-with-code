using ChatWithCode.Core.Abstractions;

namespace ChatWithCode.Worker;

/// <summary>
/// Placeholder until the indexer is added in phase 2. Nothing in phase 1 queues jobs,
/// but if one is queued manually it fails with a clear message instead of disappearing.
/// </summary>
internal sealed class NotAvailableIndexJobHandler : IIndexJobHandler
{
    public Task HandleAsync(IndexJobContext job, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The indexer is not installed in this build yet (planned for phase 2).");
}
