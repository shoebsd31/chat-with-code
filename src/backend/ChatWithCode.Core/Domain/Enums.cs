namespace ChatWithCode.Core.Domain;

/// <summary>Where a settings row applies. Project settings override the organization defaults.</summary>
public enum SettingScope
{
    Organization,
    Project,
}

public enum ModelProviderKind
{
    AzureOpenAI,
}

public enum VectorStoreKind
{
    PgVector,
    AzureAISearch,
}

public enum IndexJobType
{
    /// <summary>Re-read every file on the branch.</summary>
    Full,

    /// <summary>Only files changed since the last indexed commit.</summary>
    Incremental,
}

/// <summary>What started an index job. Runs started by the periodic check never re-split unchanged files.</summary>
public enum IndexJobTrigger
{
    /// <summary>An admin (settings page, command line, enabling a branch).</summary>
    Admin,

    /// <summary>An Azure DevOps push notification.</summary>
    Push,

    /// <summary>The periodic check for new commits.</summary>
    Poll,
}

/// <summary>What a model call recorded in the usage ledger was for.</summary>
public enum UsageKind
{
    /// <summary>Answering a question (counts towards the user's allowance).</summary>
    Answer,

    /// <summary>Rewriting a follow-up question for search (counts towards the user's allowance).</summary>
    Rewrite,

    /// <summary>Writing a branch's starter questions (organization usage, no user).</summary>
    StarterQuestions,
}

public enum IndexJobState
{
    Queued,
    Running,
    Succeeded,
    Failed,
}

public enum BranchIndexStatus
{
    NotIndexed,
    Queued,
    Indexing,
    Ready,
    Failed,
}

public enum MessageRole
{
    User,
    Assistant,
}
