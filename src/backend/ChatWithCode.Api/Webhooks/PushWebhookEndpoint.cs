using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChatWithCode.Core.Domain;
using ChatWithCode.Core.Options;
using ChatWithCode.Data;
using ChatWithCode.Data.Queue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ChatWithCode.Api.Webhooks;

/// <summary>
/// Receives Azure DevOps "Code pushed" service hook notifications and queues re-indexing of
/// the pushed branches that are indexed. Protected with basic authentication
/// (WEBHOOK_USERNAME / WEBHOOK_PASSWORD), since Azure DevOps service hooks support it.
/// The payload is untrusted: it can only queue work for branches an admin already chose.
/// </summary>
public static partial class PushWebhookEndpoint
{
    public const string Path = "/api/webhooks/ado";

    public static IEndpointRouteBuilder MapPushWebhook(this IEndpointRouteBuilder app)
    {
        app.MapPost(Path, HandleAsync).DisableRateLimiting();
        return app;
    }

    public sealed record PushResult(string Message, int Queued);

    private static async Task<IResult> HandleAsync(
        HttpRequest request,
        IOptions<SecurityOptions> security,
        ChatWithCodeDbContext db,
        IndexJobQueue queue,
        TimeProvider clock,
        ILogger<PushResult> logger,
        CancellationToken ct)
    {
        var options = security.Value;
        if (string.IsNullOrWhiteSpace(options.WebhookUsername) || string.IsNullOrWhiteSpace(options.WebhookPassword))
        {
            return Results.Problem("Push notifications aren't set up on this service. Set WEBHOOK_USERNAME and WEBHOOK_PASSWORD.",
                statusCode: StatusCodes.Status404NotFound, title: "Not set up");
        }

        if (!IsAuthorized(request, options.WebhookUsername, options.WebhookPassword))
        {
            return Results.Problem("Wrong or missing basic authentication credentials.", statusCode: StatusCodes.Status401Unauthorized, title: "Not signed in");
        }

        JsonDocument payload;
        try
        {
            payload = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
        }
        catch (JsonException)
        {
            return Results.Problem("The body isn't JSON.", statusCode: StatusCodes.Status400BadRequest, title: "Bad request");
        }

        using (payload)
        {
            var root = payload.RootElement;
            if (!root.TryGetProperty("eventType", out var eventType) || eventType.GetString() != "git.push")
            {
                return Results.Ok(new PushResult("Ignored: only \"Code pushed\" (git.push) notifications are used.", 0));
            }

            if (!root.TryGetProperty("resource", out var resource) ||
                !resource.TryGetProperty("repository", out var repository) ||
                !repository.TryGetProperty("id", out var repoIdElement) || !Guid.TryParse(repoIdElement.GetString(), out var repoId) ||
                !resource.TryGetProperty("refUpdates", out var refUpdates) || refUpdates.ValueKind != JsonValueKind.Array)
            {
                return Results.Problem("The notification has no repository or ref updates. In the service hook, set 'Resource details to send' to All.",
                    statusCode: StatusCodes.Status400BadRequest, title: "Bad request");
            }

            var repo = await db.Repositories.Include(r => r.Branches).SingleOrDefaultAsync(r => r.AdoRepositoryId == repoId && r.IsEnabled, ct);
            if (repo is null)
            {
                return Results.Ok(new PushResult("Received. This repository isn't indexed, so nothing was queued.", 0));
            }

            repo.LastPushNotificationAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);

            var queued = 0;
            foreach (var update in refUpdates.EnumerateArray())
            {
                var name = update.TryGetProperty("name", out var n) ? n.GetString() : null;
                var newCommit = update.TryGetProperty("newObjectId", out var c) ? c.GetString() : null;
                var branch = repo.Branches.FirstOrDefault(b => b.BranchName == name);
                if (branch is null || newCommit is null || !CommitId().IsMatch(newCommit) || newCommit.All(ch => ch == '0'))
                {
                    continue; // not indexed, malformed, or the branch was deleted
                }

                if (branch.LastIndexedCommit == newCommit)
                {
                    continue;
                }

                await queue.EnqueueAsync(branch.Id, IndexJobType.Incremental, newCommit, ct);
                queued++;
            }

            logger.LogInformation("Push notification for {Repository}: {Queued} branch(es) queued", repo.Name, queued);
            return Results.Ok(new PushResult(queued > 0 ? $"Queued {queued} branch(es) for indexing." : "Received. No indexed branch changed.", queued));
        }
    }

    private static bool IsAuthorized(HttpRequest request, string username, string password)
    {
        if (!AuthenticationHeaderValue.TryParse(request.Headers.Authorization, out var header) ||
            !string.Equals(header.Scheme, "Basic", StringComparison.OrdinalIgnoreCase) || header.Parameter is null)
        {
            return false;
        }

        string supplied;
        try
        {
            supplied = Encoding.UTF8.GetString(Convert.FromBase64String(header.Parameter));
        }
        catch (FormatException)
        {
            return false;
        }

        // Compare hashes in constant time, so timing doesn't reveal the credentials.
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes($"{username}:{password}"));
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    [GeneratedRegex("^[0-9a-fA-F]{40}$")]
    private static partial Regex CommitId();
}
