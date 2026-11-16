namespace ChatWithCode.Api.Chat;

// ---- Requests ----

/// <summary>A repository branch to search. RepositoryId is the Azure DevOps repository id.</summary>
public sealed record ChatTarget(Guid RepositoryId, string Branch);

/// <summary>
/// Ask a question. Leave ConversationId empty to start a new conversation. Targets are required
/// for a new conversation; for an existing one they default to the conversation's targets.
/// </summary>
public sealed record AskRequest(string Question, Guid? ConversationId = null, IReadOnlyList<ChatTarget>? Targets = null);

// ---- Streamed events (Server-Sent Events) ----

/// <summary>event: conversation — sent first.</summary>
public sealed record ConversationEvent(Guid ConversationId, string Title, string Question);

/// <summary>event: delta — a piece of the answer.</summary>
public sealed record DeltaEvent(string Text);

/// <summary>event: citations — the sources, after the answer. When none were used, these are the closest files.</summary>
public sealed record CitationsEvent(IReadOnlyList<CitationDto> Citations, bool AnswerFound);

/// <summary>event: done — the answer was saved.</summary>
public sealed record DoneEvent(Guid MessageId, int? PromptTokens, int? CompletionTokens);

/// <summary>event: error — something went wrong; the message is safe to show.</summary>
public sealed record ErrorEvent(string Message);

// ---- Responses ----

public sealed record CitationDto(
    int Number,
    Guid RepositoryId,
    string? Project,
    string? Repository,
    string Branch,
    string Path,
    int StartLine,
    int EndLine,
    string? Symbol,
    Uri? Url,
    bool Used);

public sealed record MeDto(string Id, string DisplayName, bool IsAdmin);

public sealed record BranchDto(string Name, bool IsDefault, string Status, DateTimeOffset? LastIndexedAt, int FileCount, int ProgressDone, int ProgressTotal);

public sealed record RepositoryDto(Guid Id, Guid ProjectId, string Project, string Name, IReadOnlyList<BranchDto> Branches);

public sealed record ConversationSummaryDto(Guid Id, string Title, DateTimeOffset UpdatedAt, IReadOnlyList<ChatTarget> Targets);

public sealed record MessageDto(Guid Id, string Role, string Content, DateTimeOffset CreatedAt, IReadOnlyList<CitationDto> Citations);

public sealed record ConversationDto(Guid Id, string Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<ChatTarget> Targets, IReadOnlyList<MessageDto> Messages);
