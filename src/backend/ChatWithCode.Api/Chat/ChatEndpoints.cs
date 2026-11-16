using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ChatWithCode.Api.Auth;
using ChatWithCode.Data;
using Microsoft.EntityFrameworkCore;

namespace ChatWithCode.Api.Chat;

public static class ChatEndpoints
{
    public const string RateLimitPolicy = "chat";

    public static IEndpointRouteBuilder MapChatEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api")
            .RequireAuthorization()
            .AddEndpointFilter<RequireUserAccessFilter>();

        api.MapGet("/me", (HttpContext http) =>
        {
            var user = http.GetUserAccess().User;
            return new MeDto(user.Id, user.DisplayName);
        });

        // Indexed repositories this user can read, for the repository picker.
        api.MapGet("/repositories", async (HttpContext http, ChatWithCodeDbContext db, CancellationToken ct) =>
        {
            var access = http.GetUserAccess();
            var readable = access.ReadableRepositories.Keys.ToList();
            var repos = await db.Repositories.AsNoTracking().Include(r => r.Branches)
                .Where(r => r.IsEnabled && readable.Contains(r.AdoRepositoryId))
                .OrderBy(r => r.ProjectName).ThenBy(r => r.Name)
                .ToListAsync(ct);
            return repos.Select(r => new RepositoryDto(
                r.AdoRepositoryId, r.AdoProjectId, r.ProjectName, r.Name,
                r.Branches.OrderByDescending(b => b.IsDefault).ThenBy(b => b.BranchName)
                    .Select(b => new BranchDto(b.BranchName, b.IsDefault, b.Status.ToString(), b.LastIndexedAt, b.FileCount, b.ProgressDone, b.ProgressTotal))
                    .ToList())).ToList();
        });

        api.MapGet("/conversations", async (HttpContext http, ChatWithCodeDbContext db, CancellationToken ct) =>
        {
            var userId = http.GetUserAccess().User.Id;
            var list = await db.Conversations.AsNoTracking()
                .Where(c => c.UserId == userId)
                .OrderByDescending(c => c.UpdatedAt)
                .Take(100)
                .ToListAsync(ct);
            return list.Select(c => new ConversationSummaryDto(c.Id, c.Title, c.UpdatedAt, c.Targets.Select(t => new ChatTarget(t.RepositoryId, t.Branch)).ToList()));
        });

        api.MapGet("/conversations/{id:guid}", async (Guid id, HttpContext http, ChatWithCodeDbContext db, ChatService chat, CancellationToken ct) =>
        {
            var userId = http.GetUserAccess().User.Id;
            var conversation = await db.Conversations.AsNoTracking()
                .Include(c => c.Messages).ThenInclude(m => m.Citations)
                .AsSplitQuery()
                .SingleOrDefaultAsync(c => c.Id == id && c.UserId == userId, ct);
            if (conversation is null)
            {
                return Results.Problem("That conversation doesn't exist anymore.", statusCode: StatusCodes.Status404NotFound, title: "Not found");
            }

            var repoIds = conversation.Messages.SelectMany(m => m.Citations).Select(c => c.RepositoryId).Distinct().ToList();
            var repos = await db.Repositories.AsNoTracking().Where(r => repoIds.Contains(r.AdoRepositoryId)).ToListAsync(ct);
            return Results.Ok(new ConversationDto(
                conversation.Id, conversation.Title, conversation.CreatedAt, conversation.UpdatedAt,
                conversation.Targets.Select(t => new ChatTarget(t.RepositoryId, t.Branch)).ToList(),
                conversation.Messages.OrderBy(m => m.CreatedAt).Select(m => new MessageDto(
                    m.Id, m.Role.ToString(), m.Content, m.CreatedAt,
                    m.Citations.OrderBy(c => c.Number).Select(c => chat.ToDto(c, repos)).ToList())).ToList()));
        });

        api.MapDelete("/conversations/{id:guid}", async (Guid id, HttpContext http, ChatWithCodeDbContext db, CancellationToken ct) =>
        {
            var userId = http.GetUserAccess().User.Id;
            var deleted = await db.Conversations.Where(c => c.Id == id && c.UserId == userId).ExecuteDeleteAsync(ct);
            return deleted == 0
                ? Results.Problem("That conversation doesn't exist anymore.", statusCode: StatusCodes.Status404NotFound, title: "Not found")
                : Results.NoContent();
        });

        // Ask a question. The answer streams back as Server-Sent Events:
        // conversation → delta… → citations → done (or error).
        api.MapPost("/chat", async (AskRequest request, HttpContext http, ChatService chat) =>
        {
            PreparedChat prepared;
            try
            {
                prepared = await chat.PrepareAsync(http.GetUserAccess(), request, http.RequestAborted);
            }
            catch (ChatRequestException ex)
            {
                return Results.Problem(ex.Message, statusCode: ex.StatusCode, title: "Can't answer this request");
            }

            return TypedResults.ServerSentEvents(Stream(chat, prepared, http.RequestAborted));
        }).RequireRateLimiting(RateLimitPolicy);

        return app;
    }

    /// <summary>
    /// Runs the answer on a background task that writes events to a channel, and relays them.
    /// The relay waits for that task before finishing, so it never outlives the request.
    /// </summary>
    private static async IAsyncEnumerable<SseItem<object>> Stream(ChatService chat, PreparedChat prepared, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<SseItem<object>>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var producer = Task.Run(() => chat.StreamAsync(prepared, channel.Writer, cancellationToken), CancellationToken.None);
        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return item;
            }
        }
        finally
        {
            await producer;
        }
    }
}
