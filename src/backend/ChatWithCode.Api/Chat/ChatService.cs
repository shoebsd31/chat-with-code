using System.Net.ServerSentEvents;
using System.Text;
using System.Threading.Channels;
using ChatWithCode.Api.Auth;
using ChatWithCode.Core.Abstractions;
using ChatWithCode.Core.Chat;
using ChatWithCode.Core.Domain;
using ChatWithCode.Core.Indexing;
using ChatWithCode.Core.Options;
using ChatWithCode.Data;
using ChatWithCode.Data.Entities;
using ChatWithCode.Data.Usage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ChatWithCode.Api.Chat;

/// <summary>A request that failed validation before anything was streamed. Becomes an HTTP problem response.</summary>
public sealed class ChatRequestException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>Everything checked and loaded, ready to stream an answer.</summary>
public sealed record PreparedChat(
    Guid OrganizationId,
    UserAccess Access,
    Conversation Conversation,
    bool IsNew,
    string Question,
    IReadOnlyList<Repository> Repositories,
    IReadOnlyList<ChatTarget> Targets);

/// <summary>
/// Answers a question: rewrite follow-ups, search the allowed branches, build the prompt,
/// stream the model's answer, then save it with its citations.
/// </summary>
public sealed class ChatService(
    ChatWithCodeDbContext db,
    IModelSettingsResolver settingsResolver,
    IEmbedderFactory embedders,
    IVectorStoreFactory vectorStores,
    IChatModelFactory chatModels,
    ITokenCounter tokens,
    IOptions<ChatOptions> chatOptions,
    IOptions<AzureDevOpsOptions> adoOptions,
    TimeProvider clock,
    ILogger<ChatService> logger,
    UsageLedger usage)
{
    private readonly ChatOptions _options = chatOptions.Value;

    /// <summary>Validate the request and load the conversation. Throws <see cref="ChatRequestException"/>.</summary>
    public async Task<PreparedChat> PrepareAsync(UserAccess access, AskRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(request);
        var question = request.Question?.Trim() ?? string.Empty;
        if (question.Length == 0)
        {
            throw new ChatRequestException(400, "Type a question first.");
        }

        if (question.Length > _options.MaxQuestionLength)
        {
            throw new ChatRequestException(400, $"The question is too long. Keep it under {_options.MaxQuestionLength} characters.");
        }

        Conversation? conversation = null;
        if (request.ConversationId is { } id)
        {
            conversation = await db.Conversations.SingleOrDefaultAsync(c => c.Id == id && c.UserId == access.User.Id, cancellationToken)
                ?? throw new ChatRequestException(404, "That conversation doesn't exist anymore. Start a new one.");
        }

        var targets = (request.Targets is { Count: > 0 } requested
                ? requested
                : conversation?.Targets.Select(t => new ChatTarget(t.RepositoryId, t.Branch)).ToList())
            ?? throw new ChatRequestException(400, "Pick at least one repository to search.");
        targets = targets.Distinct().ToList();
        if (targets.Count == 0)
        {
            throw new ChatRequestException(400, "Pick at least one repository to search.");
        }

        // Every target must be readable by this user right now, and indexed.
        var repoIds = targets.Select(t => t.RepositoryId).Distinct().ToList();
        if (repoIds.FirstOrDefault(r => !access.CanRead(r)) is var denied && denied != Guid.Empty)
        {
            throw new ChatRequestException(403, "You don't have access to one of the selected repositories. Remove it and try again.");
        }

        var repositories = await db.Repositories.AsNoTracking().Include(r => r.Branches)
            .Where(r => repoIds.Contains(r.AdoRepositoryId) && r.IsEnabled)
            .ToListAsync(cancellationToken);
        foreach (var target in targets)
        {
            var repo = repositories.SingleOrDefault(r => r.AdoRepositoryId == target.RepositoryId);
            var branch = repo?.Branches.SingleOrDefault(b => b.BranchName == target.Branch);
            if (branch is null || (branch.ChunkCount == 0 && branch.Status != BranchIndexStatus.Ready))
            {
                var name = access.ReadableRepositories[target.RepositoryId].Name;
                throw new ChatRequestException(409, $"{name} ({ShortBranch(target.Branch)}) isn't indexed yet. Ask an admin to index it, or pick another branch.");
            }
        }

        var organizationId = await db.Organizations.Where(o => o.Name == adoOptions.Value.Organization).Select(o => o.Id).SingleAsync(cancellationToken);
        await CheckAllowanceAsync(organizationId, access, cancellationToken);

        var isNew = conversation is null;
        if (conversation is null)
        {
            var now = clock.GetUtcNow();
            conversation = new Conversation
            {
                Id = Guid.CreateVersion7(),
                OrganizationId = organizationId,
                UserId = access.User.Id,
                Title = Title(question),
                CreatedAt = now,
                UpdatedAt = now,
            };
        }

        conversation.Targets = targets.Select(t => new ConversationTarget { RepositoryId = t.RepositoryId, Branch = t.Branch }).ToList();
        return new PreparedChat(organizationId, access, conversation, isNew, question, repositories, targets);
    }

    /// <summary>Refuse the question when the user has used their allowance (admins aren't limited).</summary>
    private async Task CheckAllowanceAsync(Guid organizationId, UserAccess access, CancellationToken cancellationToken)
    {
        if (access.IsAdmin)
        {
            return;
        }

        var status = await usage.StatusAsync(organizationId, access.User.Id, _options.DailyCapPercent, cancellationToken);
        if (AllowanceMessage(status, clock.GetUtcNow(), _options.DailyCapPercent) is { } message)
        {
            throw new ChatRequestException(429, message);
        }
    }

    /// <summary>Why the user can't ask right now, or null when they can.</summary>
    public static string? AllowanceMessage(UsageStatus status, DateTimeOffset now, int dailyCapPercent)
    {
        if (status.MonthUsedUp)
        {
            return $"You've used this month's Chat with code allowance. It resets on {status.MonthResetsAt.ToString("d MMMM", System.Globalization.CultureInfo.InvariantCulture)} (UTC). " +
                "Ask an admin if you need more.";
        }

        if (status.DayUsedUp)
        {
            var hours = Math.Max(1, (int)Math.Ceiling((status.DayResetsAt - now).TotalHours));
            return $"You've reached today's limit ({dailyCapPercent}% of your monthly allowance). It resets at midnight UTC, in about {hours} hour{(hours == 1 ? "" : "s")}.";
        }

        return null;
    }

    /// <summary>Stream the answer as Server-Sent Events. Errors are reported as an "error" event.</summary>
    public async Task StreamAsync(PreparedChat chat, ChannelWriter<SseItem<object>> output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(output);
        try
        {
            await AnswerAsync(chat, output, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Chat request cancelled by the client");
        }
        catch (Exception ex) when (ex is SettingsIncompleteException or InvalidOperationException or NotSupportedException or AdoAccessException)
        {
            logger.LogWarning(ex, "Chat request failed");
            await output.WriteAsync(Event("error", new ErrorEvent(ex.Message)), CancellationToken.None);
        }
#pragma warning disable CA1031 // Unexpected failures still end the stream with a readable message.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            logger.LogError(ex, "Chat request failed unexpectedly");
            await output.WriteAsync(Event("error", new ErrorEvent("Something went wrong while answering. Try again in a moment.")), CancellationToken.None);
        }
        finally
        {
            output.TryComplete();
        }
    }

    private async Task AnswerAsync(PreparedChat chat, ChannelWriter<SseItem<object>> output, CancellationToken cancellationToken)
    {
        var conversation = chat.Conversation;
        var history = chat.IsNew ? [] : await HistoryAsync(conversation.Id, cancellationToken);

        // Save the question first, so it's kept even if answering fails.
        var now = clock.GetUtcNow();
        if (chat.IsNew)
        {
            db.Conversations.Add(conversation);
        }

        conversation.UpdatedAt = now;
        db.Messages.Add(new Message { Id = Guid.CreateVersion7(), ConversationId = conversation.Id, Role = MessageRole.User, Content = chat.Question, CreatedAt = now });
        await db.SaveChangesAsync(cancellationToken);
        await output.WriteAsync(Event("conversation", new ConversationEvent(conversation.Id, conversation.Title, chat.Question)), cancellationToken);

        // Settings can differ per project; the chat model comes from the first selected repository's project.
        var settingsByProject = new Dictionary<Guid, EffectiveModelSettings>();
        foreach (var projectId in chat.Repositories.Select(r => r.AdoProjectId).Distinct())
        {
            settingsByProject[projectId] = await settingsResolver.ResolveAsync(projectId, cancellationToken);
        }

        var primaryProjectId = chat.Repositories.Single(r => r.AdoRepositoryId == chat.Targets[0].RepositoryId).AdoProjectId;
        var primary = settingsByProject[primaryProjectId];
        var chatModel = chatModels.Create(primary);

        // A follow-up like "and where is it tested?" is rewritten so search knows what "it" is.
        var searchText = chat.Question;
        if (history.Count > 0)
        {
            searchText = await RewriteAsync(chat, primaryProjectId, primary, chatModel, history, cancellationToken);
        }

        var sources = await SearchAsync(chat, settingsByProject, searchText, cancellationToken);

        // Answer.
        var answer = new StringBuilder();
        int? promptTokens = null, completionTokens = null;
        if (sources.Count == 0)
        {
            answer.Append(PromptBuilder.NotFoundAnswer);
            await output.WriteAsync(Event("delta", new DeltaEvent(PromptBuilder.NotFoundAnswer)), cancellationToken);
        }
        else
        {
            var request = PromptBuilder.Answer(chat.Question, sources, history, _options.MaxAnswerTokens);
            try
            {
                await foreach (var delta in chatModel.StreamAsync(request, cancellationToken))
                {
                    promptTokens = delta.PromptTokens ?? promptTokens;
                    completionTokens = delta.CompletionTokens ?? completionTokens;
                    if (delta.Text.Length > 0)
                    {
                        answer.Append(delta.Text);
                        await output.WriteAsync(Event("delta", new DeltaEvent(delta.Text)), cancellationToken);
                    }
                }
            }
            finally
            {
                // Recorded even when the user stops the answer or it fails midway: those tokens were used.
                await RecordUsageAsync(chat, primaryProjectId, primary, UsageKind.Answer, request, promptTokens, completionTokens, answer.ToString());
            }
        }

        // Citations: the sources the answer cited, or the closest files when it cited none.
        var text = answer.ToString();
        var cited = PromptBuilder.CitedNumbers(text);
        var answerFound = cited.Count > 0;
        var shown = answerFound ? sources.Where(s => cited.Contains(s.Number)).ToList() : sources.Take(5).ToList();
        var message = new Message
        {
            Id = Guid.CreateVersion7(),
            ConversationId = conversation.Id,
            Role = MessageRole.Assistant,
            Content = text,
            Model = primary.ChatDeployment,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            CreatedAt = clock.GetUtcNow(),
            Citations = shown.Select(s => new Citation
            {
                Id = Guid.CreateVersion7(),
                Number = s.Number,
                RepositoryId = s.RepositoryId,
                Branch = s.Branch,
                Path = s.Path,
                StartLine = s.StartLine,
                EndLine = s.EndLine,
                Symbol = s.Symbol,
                Score = s.Score,
                Used = answerFound,
            }).ToList(),
        };
        await output.WriteAsync(Event("citations", new CitationsEvent(message.Citations.Select(c => ToDto(c, chat.Repositories)).ToList(), answerFound)), cancellationToken);

        db.Messages.Add(message);
        conversation.UpdatedAt = message.CreatedAt;
        await db.SaveChangesAsync(CancellationToken.None);
        await output.WriteAsync(Event("done", new DoneEvent(message.Id, promptTokens, completionTokens)), CancellationToken.None);
    }

    private async Task<IReadOnlyList<SourceChunk>> SearchAsync(
        PreparedChat chat, Dictionary<Guid, EffectiveModelSettings> settingsByProject, string searchText, CancellationToken cancellationToken)
    {
        // Branches indexed with the same embedding model and store are searched together with one embedding.
        var groups = chat.Targets.GroupBy(t =>
        {
            var settings = settingsByProject[chat.Repositories.Single(r => r.AdoRepositoryId == t.RepositoryId).AdoProjectId];
            return (settings.EmbeddingModelId, settings.EmbeddingDimensions, settings.VectorStore, settings.AzureSearchEndpoint, settings.AzureSearchIndexName);
        });

        var hits = new List<SearchHit>();
        foreach (var group in groups)
        {
            var settings = settingsByProject[chat.Repositories.Single(r => r.AdoRepositoryId == group.First().RepositoryId).AdoProjectId];
            var embedding = (await embedders.Create(settings).EmbedAsync([searchText], cancellationToken))[0];
            var store = vectorStores.Create(settings);
            var allowed = group.Select(t => new RepoBranch(t.RepositoryId, t.Branch)).ToList();
            hits.AddRange(await store.HybridSearchAsync(new SearchQuery(searchText, embedding, allowed, _options.SearchCandidates), cancellationToken));
        }

        return ContextBuilder.Build(hits, tokens, _options.MaxSources, _options.ContextTokens);
    }

    private async Task<string> RewriteAsync(
        PreparedChat chat, Guid projectId, EffectiveModelSettings settings, IChatModel model, IReadOnlyList<ChatMessage> history, CancellationToken cancellationToken)
    {
        var question = chat.Question;
        try
        {
            var request = PromptBuilder.Rewrite(question, history);
            var result = await model.CompleteWithUsageAsync(request, cancellationToken);
            await RecordUsageAsync(chat, projectId, settings, UsageKind.Rewrite, request, result.PromptTokens, result.CompletionTokens, result.Text);
            var rewritten = result.Text.Trim();
            return rewritten.Length is > 0 and < 2000 ? rewritten : question;
        }
        catch (InvalidOperationException ex)
        {
            // Not fatal: search with the question as typed.
            logger.LogWarning(ex, "Could not rewrite the follow-up question; searching with it as typed");
            return question;
        }
    }

    /// <summary>Record a model call for the user's allowance. Counts the tokens itself when the model didn't report them.</summary>
    private async Task RecordUsageAsync(
        PreparedChat chat, Guid projectId, EffectiveModelSettings settings, UsageKind kind, ChatRequest request, int? promptTokens, int? completionTokens, string output)
    {
        try
        {
            var input = promptTokens ?? tokens.Count(request.SystemPrompt) + request.Messages.Sum(m => tokens.Count(m.Content) + 4);
            var produced = completionTokens ?? tokens.Count(output);
            await usage.RecordAsync(
                chat.OrganizationId, chat.Access.User.Id, chat.Access.User.DisplayName, projectId, settings.ChatDeployment ?? "unknown",
                kind, input, produced, CancellationToken.None);
        }
#pragma warning disable CA1031 // A failure to record usage must never lose the user's answer.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            logger.LogError(ex, "Couldn't record usage for {User}", chat.Access.User.Id);
        }
    }

    private async Task<IReadOnlyList<ChatMessage>> HistoryAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        if (_options.HistoryMessages == 0)
        {
            return [];
        }

        var recent = await db.Messages.AsNoTracking()
            .Where(m => m.ConversationId == conversationId)
            .OrderByDescending(m => m.CreatedAt)
            .Take(_options.HistoryMessages)
            .ToListAsync(cancellationToken);
        recent.Reverse();
        return recent.Select(m => new ChatMessage(m.Role, m.Content)).ToList();
    }

    public CitationDto ToDto(Citation citation, IReadOnlyList<Repository> repositories)
    {
        ArgumentNullException.ThrowIfNull(citation);
        ArgumentNullException.ThrowIfNull(repositories);
        var repo = repositories.FirstOrDefault(r => r.AdoRepositoryId == citation.RepositoryId);
        var url = repo is null
            ? null
            : AdoLinks.FileLines(adoOptions.Value.Organization, repo.ProjectName, repo.Name, citation.Branch, citation.Path, citation.StartLine, citation.EndLine);
        return new CitationDto(citation.Number, citation.RepositoryId, repo?.ProjectName, repo?.Name, citation.Branch, citation.Path,
            citation.StartLine, citation.EndLine, citation.Symbol, url, citation.Used);
    }

    private static SseItem<object> Event(string type, object data) => new(data, type);

    private static string Title(string question)
    {
        var line = question.Split('\n')[0].Trim();
        return line.Length <= 80 ? line : line[..77].TrimEnd() + "…";
    }

    private static string ShortBranch(string branch) =>
        branch.StartsWith("refs/heads/", StringComparison.Ordinal) ? branch["refs/heads/".Length..] : branch;
}
