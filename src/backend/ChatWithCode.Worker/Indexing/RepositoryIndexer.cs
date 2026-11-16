using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ChatWithCode.Core.Abstractions;
using ChatWithCode.Core.Domain;
using ChatWithCode.Core.Indexing;
using ChatWithCode.Core.Indexing.Chunking;
using ChatWithCode.Core.Options;
using ChatWithCode.Data;
using ChatWithCode.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ChatWithCode.Worker.Indexing;

/// <summary>
/// Indexes one branch: lists the files at the target commit, works out which changed since the
/// last run (by blob id), then downloads, chunks, embeds and stores only those, and removes
/// files that were deleted. Progress is saved after every batch, so a failed job resumes
/// where it stopped instead of starting over.
/// </summary>
public sealed class RepositoryIndexer(
    ChatWithCodeDbContext db,
    IAdoGitClient ado,
    IModelSettingsResolver settingsResolver,
    IEmbedderFactory embedders,
    IVectorStoreFactory vectorStores,
    ITokenCounter tokens,
    ChunkerSelector chunkers,
    IOptions<IndexingOptions> options,
    TimeProvider clock,
    ILogger<RepositoryIndexer> logger) : IIndexJobHandler
{
    /// <summary>Files processed between progress saves.</summary>
    internal const int FilesPerBatch = 32;

    /// <summary>Parallel file downloads.</summary>
    private const int DownloadParallelism = 8;

    /// <summary>Hard cap for one embedding input (model limit is 8,191 tokens).</summary>
    private const int MaxEmbeddingTokens = 8000;

    public async Task HandleAsync(IndexJobContext job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var stopwatch = Stopwatch.StartNew();

        var branch = await db.IndexedBranches.AsNoTracking().Include(b => b.Repository)
            .SingleOrDefaultAsync(b => b.Id == job.IndexedBranchId, cancellationToken)
            ?? throw new InvalidOperationException($"Indexed branch {job.IndexedBranchId} no longer exists.");
        var repo = branch.Repository!;
        if (!repo.IsEnabled)
        {
            logger.LogInformation("Skipping {Repository}: indexing is turned off for it", repo.Name);
            await SetStatusAsync(branch.Id, BranchIndexStatus.NotIndexed, cancellationToken);
            return;
        }

        var settings = await settingsResolver.ResolveAsync(repo.AdoProjectId, cancellationToken);
        var embedder = embedders.Create(settings);
        var store = vectorStores.Create(settings);
        await store.EnsureIndexAsync(new IndexSpec(embedder.ModelId, embedder.Dimensions), cancellationToken);

        var modelChanged = branch.EmbeddingModel is not null &&
            (branch.EmbeddingModel != embedder.ModelId || branch.EmbeddingDimensions != embedder.Dimensions);
        if (modelChanged)
        {
            logger.LogInformation("Embedding model changed ({Old} -> {New}); re-indexing {Repository} {Branch} in full",
                branch.EmbeddingModel, embedder.ModelId, repo.Name, branch.BranchName);
        }

        var full = job.Type == IndexJobType.Full || modelChanged;
        var commit = job.TargetCommit
            ?? await ado.GetBranchHeadAsync(repo.AdoRepositoryId, branch.BranchName, cancellationToken)
            ?? throw new InvalidOperationException($"Branch {branch.BranchName} no longer exists in repository {repo.ProjectName}/{repo.Name}.");

        if (!full && branch.LastIndexedCommit == commit && branch.Status != BranchIndexStatus.Failed)
        {
            logger.LogInformation("{Repository} {Branch} is already indexed at {Commit}", repo.Name, branch.BranchName, Short(commit));
            await SetStatusAsync(branch.Id, BranchIndexStatus.Ready, cancellationToken);
            return;
        }

        // 1. What's in the repository now, and what should be indexed.
        var tree = await ado.GetFilesAsync(repo.AdoRepositoryId, commit, cancellationToken);
        var filter = new FileFilter(options.Value.MaxFileSizeKb, options.Value.ExcludeGlobs, await ReadRepoIgnoreFileAsync(repo.AdoRepositoryId, tree, cancellationToken));
        var wanted = tree.Where(f => filter.Check(f.Path, f.Size) == SkipReason.None).ToDictionary(f => f.Path, StringComparer.Ordinal);

        // 2. Compare with what was indexed last time.
        var known = await db.IndexedFiles.AsNoTracking()
            .Where(f => f.IndexedBranchId == branch.Id)
            .ToDictionaryAsync(f => f.Path, f => f.BlobId, StringComparer.Ordinal, cancellationToken);
        var changed = wanted.Values
            .Where(f => full || !known.TryGetValue(f.Path, out var blob) || blob != f.BlobId)
            .OrderBy(f => f.Path, StringComparer.Ordinal)
            .ToList();
        var removed = known.Keys.Where(p => !wanted.ContainsKey(p)).ToList();

        logger.LogInformation(
            "Indexing {Repository} {Branch} at {Commit} ({Mode}): {Changed} file(s) to process, {Removed} to remove, {Unchanged} unchanged",
            repo.Name, branch.BranchName, Short(commit), full ? "full" : "incremental", changed.Count, removed.Count, wanted.Count - changed.Count);

        var target = new RepoBranch(repo.AdoRepositoryId, branch.BranchName);
        await db.IndexedBranches.Where(b => b.Id == branch.Id).ExecuteUpdateAsync(s => s
            .SetProperty(b => b.Status, BranchIndexStatus.Indexing)
            .SetProperty(b => b.ProgressDone, 0)
            .SetProperty(b => b.ProgressTotal, changed.Count), cancellationToken);

        // 3. Remove deleted files.
        if (removed.Count > 0)
        {
            await store.DeleteFilesAsync(target, removed, cancellationToken);
            foreach (var batch in removed.Chunk(500))
            {
                await db.IndexedFiles.Where(f => f.IndexedBranchId == branch.Id && batch.Contains(f.Path)).ExecuteDeleteAsync(cancellationToken);
            }
        }

        // 4. Process changed files batch by batch, saving progress after each.
        var done = 0;
        foreach (var batch in changed.Chunk(FilesPerBatch))
        {
            await IndexBatchAsync(branch.Id, target, commit, batch, embedder, store, cancellationToken);
            done += batch.Length;
            await db.IndexedBranches.Where(b => b.Id == branch.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.ProgressDone, done), cancellationToken);
        }

        // 5. Record the result.
        var totals = await db.IndexedFiles.Where(f => f.IndexedBranchId == branch.Id && f.ChunkCount > 0)
            .GroupBy(_ => 1)
            .Select(g => new { Files = g.Count(), Chunks = g.Sum(f => f.ChunkCount) })
            .SingleOrDefaultAsync(cancellationToken);
        var now = clock.GetUtcNow();
        await db.IndexedBranches.Where(b => b.Id == branch.Id).ExecuteUpdateAsync(s => s
            .SetProperty(b => b.Status, BranchIndexStatus.Ready)
            .SetProperty(b => b.LastIndexedCommit, commit)
            .SetProperty(b => b.LastIndexedAt, now)
            .SetProperty(b => b.FileCount, totals == null ? 0 : totals.Files)
            .SetProperty(b => b.ChunkCount, totals == null ? 0 : totals.Chunks)
            .SetProperty(b => b.EmbeddingModel, embedder.ModelId)
            .SetProperty(b => b.EmbeddingDimensions, embedder.Dimensions)
            .SetProperty(b => b.LastError, (string?)null), cancellationToken);

        logger.LogInformation("Indexed {Repository} {Branch} at {Commit} in {Seconds:F1}s: {Files} files, {Chunks} chunks",
            repo.Name, branch.BranchName, Short(commit), stopwatch.Elapsed.TotalSeconds, totals?.Files ?? 0, totals?.Chunks ?? 0);
    }

    private async Task IndexBatchAsync(
        Guid branchId, RepoBranch target, string commit, AdoTreeEntry[] files, IEmbedder embedder, IVectorStore store, CancellationToken cancellationToken)
    {
        // Download in parallel.
        var contents = new ConcurrentDictionary<string, byte[]>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = DownloadParallelism, CancellationToken = cancellationToken },
            async (file, ct) => contents[file.Path] = await ado.GetBlobAsync(target.RepositoryId, file.BlobId, ct));

        // Decode and chunk.
        var pending = new List<(AdoTreeEntry File, string Language, TextChunk Chunk, int Ordinal)>();
        var records = new List<IndexedFile>();
        foreach (var file in files)
        {
            var record = new IndexedFile { Id = Guid.CreateVersion7(), IndexedBranchId = branchId, Path = file.Path, BlobId = file.BlobId };
            records.Add(record);

            var skip = FileFilter.TryDecode(contents[file.Path], out var text);
            if (skip != SkipReason.None)
            {
                record.SkipReason = skip.ToString();
                continue;
            }

            var language = LanguageDetector.Detect(file.Path);
            var chunks = chunkers.For(language).Chunk(file.Path, text);
            record.ChunkCount = chunks.Count;
            for (var i = 0; i < chunks.Count; i++)
            {
                pending.Add((file, language, chunks[i], i));
            }
        }

        // Embed.
        var inputs = pending.Select(p => EmbeddingText(p.File.Path, p.Language, p.Chunk)).ToList();
        var vectors = inputs.Count == 0 ? [] : await embedder.EmbedAsync(inputs, cancellationToken);
        if (vectors.Count != inputs.Count)
        {
            throw new InvalidOperationException($"Expected {inputs.Count} embeddings but received {vectors.Count}.");
        }

        // Store: replace each file's chunks, then record the file versions.
        var paths = files.Select(f => f.Path).ToList();
        await store.DeleteFilesAsync(target, paths, cancellationToken);
        await store.UpsertAsync(
            pending.Select((p, i) => new CodeChunk(
                ChunkId(target, p.File.Path, p.Ordinal), target, p.File.Path, p.Language,
                p.Chunk.StartLine, p.Chunk.EndLine, p.Chunk.Symbol, p.Chunk.Content, commit, vectors[i])).ToList(),
            cancellationToken);

        await db.IndexedFiles.Where(f => f.IndexedBranchId == branchId && paths.Contains(f.Path)).ExecuteDeleteAsync(cancellationToken);
        db.IndexedFiles.AddRange(records);
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();
    }

    /// <summary>What gets embedded: a short header (file, language, symbol) plus the code, capped to the model limit.</summary>
    internal string EmbeddingText(string path, string language, TextChunk chunk)
    {
        var header = new StringBuilder()
            .Append("File: ").Append(path).Append('\n')
            .Append("Language: ").Append(language).Append('\n');
        if (chunk.Symbol is not null)
        {
            header.Append("Symbol: ").Append(chunk.Symbol).Append('\n');
        }

        var text = header.Append('\n').Append(chunk.Content).ToString();
        while (tokens.Count(text) > MaxEmbeddingTokens)
        {
            text = text[..(text.Length * 9 / 10)];
        }

        return text;
    }

    /// <summary>Stable id for a chunk: the same file position always gets the same id.</summary>
    internal static string ChunkId(RepoBranch target, string path, int ordinal)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{target.RepositoryId:N}|{target.Branch}|{path}|{ordinal}"));
        return Convert.ToHexStringLower(bytes)[..32];
    }

    private async Task<string?> ReadRepoIgnoreFileAsync(Guid repositoryId, IReadOnlyList<AdoTreeEntry> tree, CancellationToken cancellationToken)
    {
        var entry = tree.FirstOrDefault(f => f.Path == FileFilter.RepoIgnoreFileName);
        if (entry is null)
        {
            return null;
        }

        var bytes = await ado.GetBlobAsync(repositoryId, entry.BlobId, cancellationToken);
        return FileFilter.TryDecode(bytes, out var text) == SkipReason.None ? text : null;
    }

    private Task<int> SetStatusAsync(Guid branchId, BranchIndexStatus status, CancellationToken cancellationToken) =>
        db.IndexedBranches.Where(b => b.Id == branchId)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, status).SetProperty(b => b.LastError, (string?)null), cancellationToken);

    private static string Short(string commit) => commit.Length > 8 ? commit[..8] : commit;
}
