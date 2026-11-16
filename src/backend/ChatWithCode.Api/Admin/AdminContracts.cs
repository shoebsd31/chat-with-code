namespace ChatWithCode.Api.Admin;

/// <summary>Whether a secret is stored, and its last characters so admins can recognize it. Never the value.</summary>
public sealed record SecretState(bool IsSet, string? Hint);

/// <summary>
/// Stored settings at one level. On a project, a null value means "use the organization default".
/// </summary>
public sealed record ModelSettingsDto(
    string? AzureOpenAIEndpoint,
    string? ChatDeployment,
    string? EmbeddingDeployment,
    int? EmbeddingDimensions,
    bool? UseManagedIdentity,
    SecretState ApiKey,
    string? VectorStore,
    string? AzureSearchEndpoint,
    string? AzureSearchIndexName,
    SecretState AzureSearchApiKey,
    DateTimeOffset? UpdatedAt,
    string? UpdatedBy);

/// <param name="Organization">Organization defaults.</param>
/// <param name="Project">The project's overrides, when a project was asked for.</param>
/// <param name="CanEdit">Whether the signed-in user is a Chat with code admin.</param>
/// <param name="Problem">Why the effective settings can't be used yet (missing values), or null when complete.</param>
public sealed record SettingsResponse(ModelSettingsDto Organization, ModelSettingsDto? Project, bool CanEdit, string? Problem);

/// <summary>
/// New values for one level. Text fields: the value, or null/empty to clear (on a project: to inherit).
/// Secrets: null keeps the stored value, "" removes it, anything else replaces it.
/// </summary>
public sealed record SaveSettingsRequest(
    string? AzureOpenAIEndpoint,
    string? ChatDeployment,
    string? EmbeddingDeployment,
    int? EmbeddingDimensions,
    bool? UseManagedIdentity,
    string? ApiKey,
    string? VectorStore,
    string? AzureSearchEndpoint,
    string? AzureSearchIndexName,
    string? AzureSearchApiKey);

/// <param name="ReindexQueued">Branches queued for a full re-index because the embedding settings changed.</param>
public sealed record SaveSettingsResponse(SettingsResponse Settings, int ReindexQueued);

public sealed record TestSettingsRequest(Guid? ProjectId);

public sealed record CheckResult(bool Ok, string Message);

public sealed record TestSettingsResponse(CheckResult Embedding, CheckResult Chat);

public sealed record AdminBranchDto(
    string Name,
    bool IsDefault,
    string Status,
    int ProgressDone,
    int ProgressTotal,
    int FileCount,
    int ChunkCount,
    DateTimeOffset? LastIndexedAt,
    string? LastError);

/// <param name="Indexed">Whether any branch of the repository is in the index.</param>
public sealed record AdminRepositoryDto(Guid Id, string Name, string? DefaultBranch, bool Indexed, IReadOnlyList<AdminBranchDto> Branches);

/// <summary>The branches to keep indexed. An empty list removes the repository from the index.</summary>
public sealed record SetBranchesRequest(IReadOnlyList<string> Branches);

public sealed record ReindexRequest(string Branch, bool Full);
