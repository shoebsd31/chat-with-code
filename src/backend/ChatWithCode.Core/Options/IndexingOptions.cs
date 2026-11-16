using System.ComponentModel.DataAnnotations;

namespace ChatWithCode.Core.Options;

/// <summary>Indexer behaviour. Bound from the "Indexing" configuration section.</summary>
public sealed class IndexingOptions
{
    public const string SectionName = "Indexing";

    /// <summary>Files larger than this are skipped.</summary>
    [Range(1, 10_240)]
    public int MaxFileSizeKb { get; set; } = 500;

    /// <summary>
    /// Check branch heads for new commits on a timer. Needed when Azure DevOps can't reach
    /// this backend to deliver push notifications (for example, inside a private network).
    /// </summary>
    public bool PollingEnabled { get; set; } = true;

    /// <summary>
    /// Minutes between checks. 0 checks once when the worker starts and then stops
    /// (handy while testing); 5 suits production.
    /// </summary>
    [Range(0, 1_440)]
    public int PollIntervalMinutes { get; set; }

    /// <summary>How often the worker looks for queued jobs when the queue is empty.</summary>
    [Range(1, 300)]
    public int QueuePollSeconds { get; set; } = 3;

    /// <summary>A job still "running" after this long is assumed to be from a crashed worker and is re-queued.</summary>
    [Range(1, 1_440)]
    public int StaleJobMinutes { get; set; } = 60;

    /// <summary>Extra glob patterns to skip, in addition to the built-in list.</summary>
    public List<string> ExcludeGlobs { get; set; } = [];
}
