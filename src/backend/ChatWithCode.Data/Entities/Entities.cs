using ChatWithCode.Core.Domain;

namespace ChatWithCode.Data.Entities;

/// <summary>The Azure DevOps organization this backend serves (one per self-hosted install).</summary>
public sealed class Organization
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Model and vector store settings. One row for the organization defaults (ProjectId = null)
/// and at most one per project. A null value on a project row means "use the organization default".
/// Secrets are stored encrypted (ASP.NET Data Protection) and are never returned by the API.
/// </summary>
public sealed class ModelSettings
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public SettingScope Scope { get; set; }

    /// <summary>Azure DevOps project id. Null for the organization-level row.</summary>
    public Guid? ProjectId { get; set; }

    public ModelProviderKind? Provider { get; set; }
    public string? AzureOpenAIEndpoint { get; set; }
    public string? ChatDeployment { get; set; }
    public string? EmbeddingDeployment { get; set; }
    public int? EmbeddingDimensions { get; set; }
    public bool? UseManagedIdentity { get; set; }
    public string? ApiKeyProtected { get; set; }

    public VectorStoreKind? VectorStore { get; set; }
    public string? AzureSearchEndpoint { get; set; }
    public string? AzureSearchIndexName { get; set; }
    public string? AzureSearchApiKeyProtected { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>An Azure DevOps Git repository that an admin enabled for indexing.</summary>
public sealed class Repository
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid AdoRepositoryId { get; set; }
    public Guid AdoProjectId { get; set; }
    public required string ProjectName { get; set; }
    public required string Name { get; set; }
    public string? DefaultBranch { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When Azure DevOps last sent a push notification for this repository.</summary>
    public DateTimeOffset? LastPushNotificationAt { get; set; }

    public List<IndexedBranch> Branches { get; set; } = [];
}

/// <summary>A branch of a repository and how far it has been indexed.</summary>
public sealed class IndexedBranch
{
    public Guid Id { get; set; }
    public Guid RepositoryId { get; set; }
    public Repository? Repository { get; set; }
    public required string BranchName { get; set; }
    public bool IsDefault { get; set; }
    public BranchIndexStatus Status { get; set; } = BranchIndexStatus.NotIndexed;
    public string? LastIndexedCommit { get; set; }
    public DateTimeOffset? LastIndexedAt { get; set; }
    public int FileCount { get; set; }
    public int ChunkCount { get; set; }
    public string? EmbeddingModel { get; set; }
    public int? EmbeddingDimensions { get; set; }
    public string? LastError { get; set; }

    /// <summary>Files processed so far in the running index job, for a progress bar.</summary>
    public int ProgressDone { get; set; }

    /// <summary>Files the running index job has to process.</summary>
    public int ProgressTotal { get; set; }

    /// <summary>Starter questions for the empty chat, written by the chat model from the README.</summary>
    public List<string>? SuggestedQuestions { get; set; }

    /// <summary>What the questions were written from ("readme:&lt;blob id&gt;" or "layout:&lt;hash&gt;"), so they're only rewritten when it changes.</summary>
    public string? SuggestionsSource { get; set; }
}

/// <summary>
/// Which version (blob id) of each file was last indexed on a branch. The next run only
/// re-processes files whose blob id changed, and removes files that disappeared.
/// </summary>
public sealed class IndexedFile
{
    public Guid Id { get; set; }
    public Guid IndexedBranchId { get; set; }
    public required string Path { get; set; }
    public required string BlobId { get; set; }
    public int ChunkCount { get; set; }

    /// <summary>
    /// The splitter version that cut this file (ChunkerSelector.LatestVersion at the time). Files cut with
    /// an older version than their language needs are re-split once, on a run started by a push or an admin.
    /// </summary>
    public int SplitterVersion { get; set; } = 1;

    /// <summary>Set when the file was looked at but not indexed (binary, not UTF-8, …).</summary>
    public string? SkipReason { get; set; }
}

/// <summary>A chunk of code with its embedding, stored by the pgvector vector store.</summary>
public sealed class CodeChunkRecord
{
    public const int Dimensions = 1536;

    public required string Id { get; set; }

    /// <summary>Azure DevOps repository id.</summary>
    public Guid RepositoryId { get; set; }
    public required string Branch { get; set; }
    public required string Path { get; set; }
    public required string Language { get; set; }
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public string? Symbol { get; set; }
    public required string Content { get; set; }
    public required string CommitId { get; set; }
    public required Pgvector.Vector Embedding { get; set; }

    /// <summary>Full-text index over path, symbol and content (generated by the database).</summary>
    public NpgsqlTypes.NpgsqlTsVector? SearchVector { get; set; }
}

/// <summary>A unit of indexing work. The table doubles as the job queue.</summary>
public sealed class IndexJob
{
    public Guid Id { get; set; }
    public Guid IndexedBranchId { get; set; }
    public IndexedBranch? IndexedBranch { get; set; }
    public IndexJobType Type { get; set; }
    public IndexJobTrigger Trigger { get; set; }
    public IndexJobState State { get; set; } = IndexJobState.Queued;
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; } = 3;
    public string? TargetCommit { get; set; }
    public string? Error { get; set; }
    public string? LockedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset AvailableAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

public sealed class Conversation
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }

    /// <summary>Azure DevOps user id of the owner.</summary>
    public required string UserId { get; set; }
    public required string Title { get; set; }

    /// <summary>The repositories and branches this conversation searches.</summary>
    public List<ConversationTarget> Targets { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<Message> Messages { get; set; } = [];
}

public sealed class ConversationTarget
{
    public Guid RepositoryId { get; set; }
    public required string Branch { get; set; }
}

public sealed class Message
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public MessageRole Role { get; set; }
    public required string Content { get; set; }
    public string? Model { get; set; }
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public List<Citation> Citations { get; set; } = [];
}

/// <summary>A source an answer used, shown as [n] in the answer and linked to the file in Azure DevOps.</summary>
public sealed class Citation
{
    public Guid Id { get; set; }
    public Guid MessageId { get; set; }
    public int Number { get; set; }
    public Guid RepositoryId { get; set; }
    public required string Branch { get; set; }
    public required string Path { get; set; }
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public string? Symbol { get; set; }
    public double Score { get; set; }

    /// <summary>True when the answer cited this source; false for "closest files" shown when nothing was found.</summary>
    public bool Used { get; set; }
}

/// <summary>
/// One chat model call, for usage allowances and the admin usage report. Kept separately from chat
/// history (which users can delete and which expires), so deleting a conversation doesn't reset usage.
/// </summary>
public sealed class UsageRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }

    /// <summary>Azure DevOps user id; null for organization usage such as starter questions.</summary>
    public string? UserId { get; set; }
    public string? UserName { get; set; }
    public Guid? ProjectId { get; set; }

    /// <summary>The chat deployment that answered (prices are set per deployment).</summary>
    public required string Model { get; set; }
    public UsageKind Kind { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Usage allowance settings for the organization. No row, or no allowance, means no limit.</summary>
public sealed class UsageSettings
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }

    /// <summary>Currency the prices and allowances are entered in. Only a label: nothing is converted.</summary>
    public string Currency { get; set; } = "USD";

    /// <summary>Monthly allowance per user, in <see cref="Currency"/>. Null turns limits off.</summary>
    public decimal? MonthlyAllowance { get; set; }

    /// <summary>Price per million tokens for each chat deployment.</summary>
    public List<ModelPrice> Prices { get; set; } = [];

    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

public sealed class ModelPrice
{
    public required string Model { get; set; }
    public decimal InputPerMillion { get; set; }
    public decimal OutputPerMillion { get; set; }
}

/// <summary>A user's own monthly allowance, instead of the organization's.</summary>
public sealed class UserAllowance
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public required string UserId { get; set; }
    public string? UserName { get; set; }
    public decimal MonthlyAllowance { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}
