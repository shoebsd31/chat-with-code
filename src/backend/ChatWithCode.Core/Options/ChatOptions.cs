using System.ComponentModel.DataAnnotations;

namespace ChatWithCode.Core.Options;

/// <summary>Chat behaviour. Bound from the "Chat" configuration section.</summary>
public sealed class ChatOptions
{
    public const string SectionName = "Chat";

    /// <summary>Conversations with no activity for this many days are deleted.</summary>
    [Range(1, 3650)]
    public int RetentionDays { get; set; } = 30;

    /// <summary>Chunks fetched from search before picking context.</summary>
    [Range(5, 200)]
    public int SearchCandidates { get; set; } = 40;

    /// <summary>Most chunks put in front of the model.</summary>
    [Range(1, 50)]
    public int MaxSources { get; set; } = 12;

    /// <summary>Token budget for source code in the prompt.</summary>
    [Range(1000, 100_000)]
    public int ContextTokens { get; set; } = 8000;

    /// <summary>Upper limit for one answer (reasoning models count their thinking here too).</summary>
    [Range(256, 32_000)]
    public int MaxAnswerTokens { get; set; } = 4000;

    /// <summary>Earlier messages included so follow-up questions make sense.</summary>
    [Range(0, 20)]
    public int HistoryMessages { get; set; } = 6;

    /// <summary>Chat requests one user may send per minute.</summary>
    [Range(1, 1000)]
    public int RequestsPerMinute { get; set; } = 20;

    /// <summary>Longest question accepted, in characters.</summary>
    [Range(100, 20_000)]
    public int MaxQuestionLength { get; set; } = 4000;
}
