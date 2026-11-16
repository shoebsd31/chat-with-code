using ChatWithCode.Api.Auth;
using ChatWithCode.Api.Chat;
using ChatWithCode.Core.Abstractions;
using ChatWithCode.Core.Domain;
using ChatWithCode.Core.Options;
using ChatWithCode.Data;
using ChatWithCode.Data.Entities;
using ChatWithCode.Data.Queue;
using ChatWithCode.Data.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ChatWithCode.Api.Admin;

/// <summary>Settings and repository management for Chat with code admins.</summary>
public sealed class AdminService(
    ChatWithCodeDbContext db,
    ISecretProtector secrets,
    IModelSettingsResolver resolver,
    IEmbedderFactory embedders,
    IChatModelFactory chatModels,
    IVectorStoreFactory vectorStores,
    IAdoGitClient ado,
    IndexJobQueue queue,
    IOptions<AzureDevOpsOptions> adoOptions,
    TimeProvider clock,
    ILogger<AdminService> logger)
{
    // ---------------- Settings ----------------

    public async Task<SettingsResponse> GetSettingsAsync(UserAccess access, Guid? projectId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(access);
        var orgId = await OrganizationIdAsync(ct);
        var rows = await db.ModelSettings.AsNoTracking()
            .Where(s => s.OrganizationId == orgId && (s.ProjectId == null || s.ProjectId == projectId))
            .ToListAsync(ct);
        var org = rows.FirstOrDefault(r => r.ProjectId is null);
        var project = projectId is null ? null : rows.FirstOrDefault(r => r.ProjectId == projectId);

        string? problem = null;
        try
        {
            var effective = ModelSettingsResolver.Merge(org, project, secrets);
            if (string.IsNullOrWhiteSpace(effective.ChatDeployment))
            {
                problem = "Chat with code isn't fully configured. Missing: chat deployment.";
            }
        }
        catch (SettingsIncompleteException ex)
        {
            problem = ex.Message;
        }

        return new SettingsResponse(
            ToDto(org),
            projectId is null ? null : ToDto(project),
            access.IsAdmin,
            problem);
    }

    public async Task<SaveSettingsResponse> SaveSettingsAsync(UserAccess access, Guid? projectId, SaveSettingsRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(request);
        RequireAdmin(access);
        Validate(request, isProject: projectId is not null);

        var orgId = await OrganizationIdAsync(ct);
        var before = await EmbeddingIdentitiesAsync(ct);

        var row = await db.ModelSettings.SingleOrDefaultAsync(s => s.OrganizationId == orgId && s.ProjectId == projectId, ct);
        if (row is null)
        {
            row = new ModelSettings
            {
                Id = Guid.CreateVersion7(),
                OrganizationId = orgId,
                Scope = projectId is null ? SettingScope.Organization : SettingScope.Project,
                ProjectId = projectId,
            };
            db.ModelSettings.Add(row);
        }

        row.Provider = projectId is null ? ModelProviderKind.AzureOpenAI : null;
        row.AzureOpenAIEndpoint = Blank(request.AzureOpenAIEndpoint);
        row.ChatDeployment = Blank(request.ChatDeployment);
        row.EmbeddingDeployment = Blank(request.EmbeddingDeployment);
        row.EmbeddingDimensions = request.EmbeddingDimensions;
        row.UseManagedIdentity = request.UseManagedIdentity;
        row.VectorStore = Blank(request.VectorStore) is { } store ? Enum.Parse<VectorStoreKind>(store, ignoreCase: true) : null;
        row.AzureSearchEndpoint = Blank(request.AzureSearchEndpoint);
        row.AzureSearchIndexName = Blank(request.AzureSearchIndexName);
        row.ApiKeyProtected = ApplySecret(row.ApiKeyProtected, request.ApiKey);
        row.AzureSearchApiKeyProtected = ApplySecret(row.AzureSearchApiKeyProtected, request.AzureSearchApiKey);
        row.UpdatedAt = clock.GetUtcNow();
        row.UpdatedBy = access.User.Email ?? access.User.DisplayName;
        await db.SaveChangesAsync(ct);

        // A different embedding model makes existing vectors useless: re-index those branches in full.
        var after = await EmbeddingIdentitiesAsync(ct);
        var queued = 0;
        foreach (var (branchId, identity) in after)
        {
            if (identity is not null && before.TryGetValue(branchId, out var old) && old != identity)
            {
                await queue.EnqueueAsync(branchId, IndexJobType.Full, cancellationToken: ct);
                queued++;
            }
        }

        logger.LogInformation("{User} saved {Scope} settings; {Queued} branch(es) queued for re-indexing",
            row.UpdatedBy, projectId is null ? "organization" : $"project {projectId}", queued);
        return new SaveSettingsResponse(await GetSettingsAsync(access, projectId, ct), queued);
    }

    public async Task DeleteProjectSettingsAsync(UserAccess access, Guid projectId, CancellationToken ct)
    {
        RequireAdmin(access);
        await db.ModelSettings.Where(s => s.ProjectId == projectId).ExecuteDeleteAsync(ct);
    }

    /// <summary>Try the effective settings with one tiny embedding and one tiny chat request.</summary>
    public async Task<TestSettingsResponse> TestSettingsAsync(UserAccess access, Guid? projectId, CancellationToken ct)
    {
        RequireAdmin(access);
        EffectiveModelSettings settings;
        try
        {
            settings = projectId is { } id ? await resolver.ResolveAsync(id, ct) : await ResolveOrganizationAsync(ct);
        }
        catch (SettingsIncompleteException ex)
        {
            var notRun = new CheckResult(false, "Not run: " + ex.Message);
            return new TestSettingsResponse(notRun, notRun);
        }

        CheckResult embedding;
        try
        {
            var vectors = await embedders.Create(settings).EmbedAsync(["Chat with code settings test"], ct);
            embedding = new CheckResult(true, $"{settings.EmbeddingDeployment} returned a {vectors[0].Length}-dimension vector.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or HttpRequestException)
        {
            embedding = new CheckResult(false, ex.Message);
        }

        CheckResult chat;
        try
        {
            var reply = await chatModels.Create(settings).CompleteAsync(
                new ChatRequest("Reply with the single word OK.", [new ChatMessage(MessageRole.User, "Test")], MaxOutputTokens: 500), ct);
            chat = new CheckResult(true, $"{settings.ChatDeployment} answered ({reply.Trim().Length} characters).");
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or SettingsIncompleteException or HttpRequestException)
        {
            chat = new CheckResult(false, ex.Message);
        }

        return new TestSettingsResponse(embedding, chat);
    }

    // ---------------- Repositories ----------------

    /// <summary>The project's repositories the admin can read, with what's indexed.</summary>
    public async Task<IReadOnlyList<AdminRepositoryDto>> GetRepositoriesAsync(UserAccess access, Guid projectId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(access);
        var readable = access.ReadableRepositories.Values.Where(r => r.ProjectId == projectId).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var ids = readable.Select(r => r.Id).ToList();
        var stored = await db.Repositories.AsNoTracking().Include(r => r.Branches)
            .Where(r => ids.Contains(r.AdoRepositoryId))
            .ToDictionaryAsync(r => r.AdoRepositoryId, ct);

        return readable.Select(r =>
        {
            stored.TryGetValue(r.Id, out var repo);
            var branches = (repo?.IsEnabled == true ? repo.Branches : [])
                .OrderByDescending(b => b.BranchName == r.DefaultBranch).ThenBy(b => b.BranchName, StringComparer.Ordinal)
                .Select(b => new AdminBranchDto(b.BranchName, b.BranchName == r.DefaultBranch, b.Status.ToString(), b.ProgressDone, b.ProgressTotal,
                    b.FileCount, b.ChunkCount, b.LastIndexedAt, b.LastError))
                .ToList();
            return new AdminRepositoryDto(r.Id, r.Name, r.DefaultBranch, branches.Count > 0, branches);
        }).ToList();
    }

    public async Task<IReadOnlyList<string>> GetBranchesAsync(UserAccess access, Guid repositoryId, CancellationToken ct)
    {
        RequireAdmin(access);
        RequireReadable(access, repositoryId);
        try
        {
            return await ado.GetBranchesAsync(repositoryId, ct);
        }
        catch (AdoAccessException ex)
        {
            throw new ChatRequestException(502, ex.Message);
        }
    }

    /// <summary>Make exactly these branches indexed. New ones are queued; removed ones are deleted from the index.</summary>
    public async Task<AdminRepositoryDto> SetBranchesAsync(UserAccess access, Guid repositoryId, SetBranchesRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireAdmin(access);
        var info = RequireReadable(access, repositoryId);
        var wanted = request.Branches.Select(NormalizeBranch).Distinct(StringComparer.Ordinal).ToList();

        if (wanted.Count > 0)
        {
            IReadOnlyList<string> existing;
            try
            {
                existing = await ado.GetBranchesAsync(repositoryId, ct);
            }
            catch (AdoAccessException ex)
            {
                throw new ChatRequestException(502, ex.Message);
            }

            var missing = wanted.Where(b => !existing.Contains(b, StringComparer.Ordinal)).ToList();
            if (missing.Count > 0)
            {
                throw new ChatRequestException(400, $"These branches don't exist in {info.Name}: {string.Join(", ", missing.Select(ShortBranch))}.");
            }
        }

        var repo = await db.Repositories.Include(r => r.Branches).SingleOrDefaultAsync(r => r.AdoRepositoryId == repositoryId, ct);
        if (repo is null)
        {
            if (wanted.Count == 0)
            {
                return (await GetRepositoriesAsync(access, info.ProjectId, ct)).Single(r => r.Id == repositoryId);
            }

            repo = new Repository
            {
                Id = Guid.CreateVersion7(),
                OrganizationId = await OrganizationIdAsync(ct),
                AdoRepositoryId = repositoryId,
                AdoProjectId = info.ProjectId,
                ProjectName = info.ProjectName,
                Name = info.Name,
                CreatedAt = clock.GetUtcNow(),
            };
            db.Repositories.Add(repo);
        }

        repo.Name = info.Name;
        repo.ProjectName = info.ProjectName;
        repo.DefaultBranch = info.DefaultBranch;
        repo.IsEnabled = true;

        var removed = repo.Branches.Where(b => !wanted.Contains(b.BranchName)).ToList();
        var added = wanted.Where(w => repo.Branches.All(b => b.BranchName != w)).Select(w => new IndexedBranch
        {
            Id = Guid.CreateVersion7(),
            RepositoryId = repo.Id,
            BranchName = w,
            IsDefault = w == info.DefaultBranch,
        }).ToList();

        db.IndexedBranches.RemoveRange(removed);
        db.IndexedBranches.AddRange(added);
        await db.SaveChangesAsync(ct);

        foreach (var branch in added)
        {
            await queue.EnqueueAsync(branch.Id, IndexJobType.Full, cancellationToken: ct);
        }

        if (removed.Count > 0)
        {
            await DeleteFromIndexAsync(repo, removed.Select(b => b.BranchName).ToList(), ct);
        }

        logger.LogInformation("{User} set indexed branches of {Repository}: {Branches}", access.User.Email ?? access.User.DisplayName, info.Name, string.Join(", ", wanted));
        return (await GetRepositoriesAsync(access, info.ProjectId, ct)).Single(r => r.Id == repositoryId);
    }

    public async Task ReindexAsync(UserAccess access, Guid repositoryId, ReindexRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireAdmin(access);
        RequireReadable(access, repositoryId);
        var branchName = NormalizeBranch(request.Branch);
        var branch = await db.IndexedBranches.AsNoTracking()
            .SingleOrDefaultAsync(b => b.Repository!.AdoRepositoryId == repositoryId && b.BranchName == branchName, ct)
            ?? throw new ChatRequestException(404, $"{ShortBranch(branchName)} isn't indexed. Add it first.");
        await queue.EnqueueAsync(branch.Id, request.Full ? IndexJobType.Full : IndexJobType.Incremental, cancellationToken: ct);
    }

    // ---------------- Helpers ----------------

    private async Task DeleteFromIndexAsync(Repository repo, IReadOnlyList<string> branches, CancellationToken ct)
    {
        try
        {
            var store = vectorStores.Create(await resolver.ResolveAsync(repo.AdoProjectId, ct));
            foreach (var branch in branches)
            {
                await store.DeleteBranchAsync(new RepoBranch(repo.AdoRepositoryId, branch), ct);
            }
        }
        catch (Exception ex) when (ex is SettingsIncompleteException or NotSupportedException or InvalidOperationException)
        {
            // Settings missing: nothing could have been indexed with them; the rows are gone either way.
            logger.LogWarning(ex, "Could not delete indexed content of removed branches of {Repository}", repo.Name);
        }
    }

    /// <summary>For every enabled branch: the embedding model and size its project's settings use now (null if incomplete).</summary>
    private async Task<Dictionary<Guid, string?>> EmbeddingIdentitiesAsync(CancellationToken ct)
    {
        var branches = await db.IndexedBranches.AsNoTracking()
            .Where(b => b.Repository!.IsEnabled && b.EmbeddingModel != null)
            .Select(b => new { b.Id, b.Repository!.AdoProjectId })
            .ToListAsync(ct);
        var byProject = new Dictionary<Guid, string?>();
        var result = new Dictionary<Guid, string?>();
        foreach (var b in branches)
        {
            if (!byProject.TryGetValue(b.AdoProjectId, out var identity))
            {
                try
                {
                    var s = await resolver.ResolveAsync(b.AdoProjectId, ct);
                    identity = $"{s.EmbeddingModelId}|{s.EmbeddingDimensions}|{s.VectorStore}";
                }
                catch (SettingsIncompleteException)
                {
                    identity = null;
                }

                byProject[b.AdoProjectId] = identity;
            }

            result[b.Id] = identity;
        }

        return result;
    }

    private async Task<EffectiveModelSettings> ResolveOrganizationAsync(CancellationToken ct)
    {
        var orgId = await OrganizationIdAsync(ct);
        var org = await db.ModelSettings.AsNoTracking().SingleOrDefaultAsync(s => s.OrganizationId == orgId && s.ProjectId == null, ct);
        return ModelSettingsResolver.Merge(org, null, secrets);
    }

    private Task<Guid> OrganizationIdAsync(CancellationToken ct) =>
        db.Organizations.Where(o => o.Name == adoOptions.Value.Organization).Select(o => o.Id).SingleAsync(ct);

    private SecretState Secret(string? protectedValue)
    {
        if (protectedValue is null)
        {
            return new SecretState(false, null);
        }

        try
        {
            var plain = secrets.Unprotect(protectedValue);
            return new SecretState(true, plain.Length > 8 ? "…" + plain[^4..] : "set");
        }
        catch (InvalidOperationException)
        {
            return new SecretState(true, "can't be read; enter it again");
        }
    }

    private ModelSettingsDto ToDto(ModelSettings? s) => new(
        s?.AzureOpenAIEndpoint,
        s?.ChatDeployment,
        s?.EmbeddingDeployment,
        s?.EmbeddingDimensions,
        s?.UseManagedIdentity,
        Secret(s?.ApiKeyProtected),
        s?.VectorStore?.ToString(),
        s?.AzureSearchEndpoint,
        s?.AzureSearchIndexName,
        Secret(s?.AzureSearchApiKeyProtected),
        s?.UpdatedAt,
        s?.UpdatedBy);

    private string? ApplySecret(string? current, string? submitted) => submitted switch
    {
        null => current,
        "" => null,
        _ => secrets.Protect(submitted.Trim()),
    };

    private static void Validate(SaveSettingsRequest r, bool isProject)
    {
        var problems = new List<string>();
        if (Blank(r.AzureOpenAIEndpoint) is { } endpoint &&
            (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps))
        {
            problems.Add("The Azure OpenAI endpoint must be an https:// address, like https://your-resource.openai.azure.com/.");
        }

        if (Blank(r.AzureSearchEndpoint) is { } search &&
            (!Uri.TryCreate(search, UriKind.Absolute, out var searchUri) || searchUri.Scheme != Uri.UriSchemeHttps))
        {
            problems.Add("The Azure AI Search endpoint must be an https:// address.");
        }

        if (r.EmbeddingDimensions is < 256 or > 3072)
        {
            problems.Add("Embedding dimensions must be between 256 and 3072.");
        }

        VectorStoreKind? store = null;
        if (Blank(r.VectorStore) is { } kind)
        {
            if (Enum.TryParse<VectorStoreKind>(kind, ignoreCase: true, out var parsed))
            {
                store = parsed;
            }
            else
            {
                problems.Add($"Unknown vector store '{kind}'. Use PgVector or AzureAISearch.");
            }
        }

        if ((store ?? VectorStoreKind.PgVector) == VectorStoreKind.PgVector && r.EmbeddingDimensions is { } dims && dims != CodeChunkRecord.Dimensions && !(isProject && store is null))
        {
            problems.Add($"The PostgreSQL (pgvector) store uses {CodeChunkRecord.Dimensions} dimensions.");
        }

        if (problems.Count > 0)
        {
            throw new ChatRequestException(400, string.Join(" ", problems));
        }
    }

    private static void RequireAdmin(UserAccess access)
    {
        if (!access.IsAdmin)
        {
            throw new ChatRequestException(403,
                "Only Chat with code admins can change these settings. Admins are listed in the service's CHAT_ADMINS setting.");
        }
    }

    private static ReadableRepository RequireReadable(UserAccess access, Guid repositoryId) =>
        access.ReadableRepositories.TryGetValue(repositoryId, out var repo)
            ? repo
            : throw new ChatRequestException(404, "That repository doesn't exist, or you can't read it.");

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeBranch(string branch) =>
        branch.StartsWith("refs/", StringComparison.Ordinal) ? branch : $"refs/heads/{branch}";

    private static string ShortBranch(string branch) =>
        branch.StartsWith("refs/heads/", StringComparison.Ordinal) ? branch["refs/heads/".Length..] : branch;
}
