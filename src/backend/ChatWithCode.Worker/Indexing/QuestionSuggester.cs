using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChatWithCode.Core.Abstractions;
using ChatWithCode.Core.Domain;
using ChatWithCode.Core.Indexing;
using ChatWithCode.Data;
using ChatWithCode.Data.Usage;
using Microsoft.EntityFrameworkCore;

namespace ChatWithCode.Worker.Indexing;

/// <summary>
/// Writes 4–5 starter questions for a branch from its README and top-level folders, shown in the
/// empty chat. They are written again only when the README (or, without one, the top-level layout)
/// changes, so a branch costs one small model call per README change. Never fails indexing:
/// problems are logged and the previous questions are kept.
/// </summary>
public sealed class QuestionSuggester(
    ChatWithCodeDbContext db,
    IAdoGitClient ado,
    IModelSettingsResolver settingsResolver,
    IChatModelFactory chatModels,
    ITokenCounter tokens,
    ILogger<QuestionSuggester> logger,
    UsageLedger? usage = null)
{
    internal const int MaxQuestions = 5;
    private const int MaxReadmeTokens = 3000;
    private const int MaxTopLevelEntries = 80;

    internal const string SystemPrompt = """
        You suggest starter questions for a chat that answers questions about one code repository,
        using only the repository's code and documentation.
        From the README and the top-level folders and files below, write 5 questions a developer new to
        this repository would ask. Make them specific to this repository (use its real names, features and
        folders), answerable from its code or docs, and varied: what it does, how a key part works, where
        something is implemented, how to run or test it, and one that asks for a diagram.
        Each question at most 90 characters. Write them in the language the README is written in.
        Reply with only a JSON array of strings, nothing else.
        """;

    /// <summary>Make sure the branch has questions for this README. Returns true when new questions were saved.</summary>
    public async Task<bool> EnsureAsync(Guid branchId, IReadOnlyList<AdoTreeEntry> tree, CancellationToken cancellationToken)
    {
        try
        {
            return await GenerateAsync(branchId, tree, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Starter questions are a nicety; nothing here may fail indexing.
        catch (Exception e)
#pragma warning restore CA1031
        {
            logger.LogWarning(e, "Couldn't write starter questions for branch {BranchId}; keeping the previous ones", branchId);
            return false;
        }
    }

    private async Task<bool> GenerateAsync(Guid branchId, IReadOnlyList<AdoTreeEntry> tree, CancellationToken cancellationToken)
    {
        var branch = await db.IndexedBranches.AsNoTracking().Include(b => b.Repository)
            .SingleOrDefaultAsync(b => b.Id == branchId, cancellationToken);
        if (branch?.Repository is not { } repo)
        {
            return false;
        }

        var readme = FindReadme(tree);
        var topLevel = TopLevel(tree);
        var source = readme is not null ? $"readme:{readme.BlobId}" : $"layout:{Hash(string.Join('\n', topLevel))}";
        if (branch.SuggestionsSource == source && branch.SuggestedQuestions is { Count: > 0 })
        {
            return false;
        }

        var settings = await settingsResolver.ResolveAsync(repo.AdoProjectId, cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.ChatDeployment))
        {
            return false; // no chat model configured yet: nothing to write them with
        }

        var readmeText = "";
        if (readme is not null && FileFilter.TryDecode(await ado.GetBlobAsync(repo.AdoRepositoryId, readme.BlobId, cancellationToken), out var text) == SkipReason.None)
        {
            readmeText = Truncate(text, MaxReadmeTokens);
        }

        var prompt = new StringBuilder()
            .Append("Repository: ").Append(repo.Name).Append('\n')
            .Append("Top-level folders and files:\n").Append(string.Join('\n', topLevel)).Append("\n\n")
            .Append(readmeText.Length > 0 ? $"README ({readme!.Path}):\n{readmeText}" : "The repository has no README.")
            .ToString();

        var request = new ChatRequest(SystemPrompt, [new ChatMessage(MessageRole.User, prompt)], MaxOutputTokens: 1000);
        var result = await chatModels.Create(settings).CompleteWithUsageAsync(request, cancellationToken);
        var reply = result.Text;
        if (usage is not null)
        {
            // Organization usage (no user): shown in the admin report, not counted against anyone's allowance.
            await usage.RecordAsync(
                repo.OrganizationId, null, null, repo.AdoProjectId, settings.ChatDeployment ?? "unknown", UsageKind.StarterQuestions,
                result.PromptTokens ?? tokens.Count(SystemPrompt) + tokens.Count(prompt), result.CompletionTokens ?? tokens.Count(reply), cancellationToken);
        }

        var questions = Parse(reply);
        if (questions.Count == 0)
        {
            logger.LogWarning("The model's starter questions for {Repository} {Branch} couldn't be read; keeping the previous ones", repo.Name, branch.BranchName);
            return false;
        }

        await db.IndexedBranches.Where(b => b.Id == branchId).ExecuteUpdateAsync(s => s
            .SetProperty(b => b.SuggestedQuestions, questions)
            .SetProperty(b => b.SuggestionsSource, source), cancellationToken);
        logger.LogInformation("Wrote {Count} starter questions for {Repository} {Branch}", questions.Count, repo.Name, branch.BranchName);
        return true;
    }

    /// <summary>The README at the root of the repository, preferring Markdown.</summary>
    internal static AdoTreeEntry? FindReadme(IReadOnlyList<AdoTreeEntry> tree) =>
        tree.Where(f => !f.Path.TrimStart('/').Contains('/', StringComparison.Ordinal) &&
                        Path.GetFileNameWithoutExtension(f.Path).Equals("readme", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => Path.GetExtension(f.Path).Equals(".md", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(f => f.Path, StringComparer.Ordinal)
            .FirstOrDefault();

    /// <summary>Top-level folders (with a trailing "/") and files, sorted.</summary>
    internal static List<string> TopLevel(IReadOnlyList<AdoTreeEntry> tree) =>
        tree.Select(f => f.Path.TrimStart('/'))
            .Select(p => p.Contains('/', StringComparison.Ordinal) ? p[..(p.IndexOf('/', StringComparison.Ordinal) + 1)] : p)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Take(MaxTopLevelEntries)
            .ToList();

    /// <summary>The JSON array of questions in the model's reply, cleaned up; empty when there is none.</summary>
    internal static List<string> Parse(string reply)
    {
        var start = reply.IndexOf('[', StringComparison.Ordinal);
        var end = reply.LastIndexOf(']');
        if (start < 0 || end <= start)
        {
            return [];
        }

        try
        {
            var items = JsonSerializer.Deserialize<List<string?>>(reply[start..(end + 1)]) ?? [];
            return items
                .Select(q => q?.Trim().Trim('"').Trim() ?? "")
                .Where(q => q.Length >= 8)
                .Select(q => q.Length > 140 ? q[..140].TrimEnd() + "…" : q)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxQuestions)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private string Truncate(string text, int maxTokens)
    {
        if (tokens.Count(text) <= maxTokens)
        {
            return text;
        }

        while (text.Length > 0 && tokens.Count(text) > maxTokens)
        {
            text = text[..(text.Length * 9 / 10)];
        }

        return text + "\n[…]";
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
}
