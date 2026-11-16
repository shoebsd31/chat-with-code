namespace ChatWithCode.Core.Abstractions;

/// <summary>Does the work for one claimed index job. Throw to fail the attempt; the queue retries it.</summary>
public interface IIndexJobHandler
{
    Task HandleAsync(IndexJobContext job, CancellationToken cancellationToken);
}

public sealed record IndexJobContext(
    Guid JobId,
    Guid IndexedBranchId,
    Domain.IndexJobType Type,
    int Attempt,
    string? TargetCommit);
