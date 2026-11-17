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
