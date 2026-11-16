using System.Security.Cryptography;
using System.Text;
using ChatWithCode.Core.Abstractions;
using ChatWithCode.Core.Domain;

namespace ChatWithCode.Tests.Infrastructure;

/// <summary>
/// An in-memory Azure DevOps repository. Each commit is a full snapshot of path → content.
/// Blob ids are content hashes, like git, so unchanged files keep their id across commits.
/// </summary>
internal sealed class FakeAdoGitClient : IAdoGitClient
{
    private readonly Dictionary<string, Dictionary<string, byte[]>> _commits = [];
    private readonly Dictionary<string, byte[]> _blobs = [];
    private readonly Dictionary<string, string> _branches = new(StringComparer.Ordinal);

    public AdoRepository Repository { get; } = new(Guid.NewGuid(), "web-app", Guid.NewGuid(), "Fabrikam", "refs/heads/main");

    public List<string> DownloadedPaths { get; } = [];

    /// <summary>Create a commit with these files and point the branch at it. Returns the commit id.</summary>
    public string Commit(Dictionary<string, string> files, string branch = "refs/heads/main") =>
        CommitBytes(files.ToDictionary(f => f.Key, f => Encoding.UTF8.GetBytes(f.Value)), branch);

    public string CommitBytes(Dictionary<string, byte[]> files, string branch = "refs/heads/main")
    {
        var id = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes($"{Guid.NewGuid()}")));
        _commits[id] = files;
        foreach (var content in files.Values)
        {
            _blobs[BlobId(content)] = content;
        }

        _branches[branch] = id;
        return id;
    }

    public Task<AdoRepository?> GetRepositoryAsync(string project, string repository, CancellationToken cancellationToken) =>
        Task.FromResult(project == Repository.ProjectName && repository == Repository.Name ? Repository : null);

    public Task<string?> GetBranchHeadAsync(Guid repositoryId, string branchRef, CancellationToken cancellationToken) =>
        Task.FromResult(_branches.TryGetValue(branchRef, out var commit) ? commit : null);

    public Task<IReadOnlyList<AdoTreeEntry>> GetFilesAsync(Guid repositoryId, string commitId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AdoTreeEntry>>(_commits[commitId].Select(f => new AdoTreeEntry(f.Key, BlobId(f.Value), f.Value.Length)).ToList());

    public Task<byte[]> GetBlobAsync(Guid repositoryId, string blobId, CancellationToken cancellationToken)
    {
        lock (DownloadedPaths)
        {
            var path = _commits.Values.SelectMany(c => c).First(f => BlobId(f.Value) == blobId).Key;
            DownloadedPaths.Add(path);
        }

        return Task.FromResult(_blobs[blobId]);
    }

    private static string BlobId(byte[] content) => Convert.ToHexStringLower(SHA1.HashData(content));
}

/// <summary>Deterministic embeddings: the same text always gives the same unit vector.</summary>
internal sealed class FakeEmbedder(string modelId = "fake:embedding", int dimensions = 1536) : IEmbedder
{
    public string ModelId { get; } = modelId;
    public int Dimensions { get; } = dimensions;
    public List<string> Inputs { get; } = [];
    public int Calls { get; private set; }

    /// <summary>Throw on this call number (1-based), to simulate an outage mid-job.</summary>
    public int? FailOnCall { get; set; }

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        Calls++;
        if (Calls == FailOnCall)
        {
            throw new InvalidOperationException("Azure OpenAI returned 503 (simulated)");
        }

        Inputs.AddRange(texts);
        return Task.FromResult<IReadOnlyList<float[]>>(texts.Select(Vector).ToList());
    }

    private float[] Vector(string text)
    {
        var random = new Random(BitConverter.ToInt32(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0));
        var v = new float[Dimensions];
        for (var i = 0; i < v.Length; i++)
        {
            v[i] = (float)(random.NextDouble() - 0.5);
        }

        var norm = MathF.Sqrt(v.Sum(x => x * x));
        return v.Select(x => x / norm).ToArray();
    }
}

internal sealed class FakeEmbedderFactory(FakeEmbedder embedder) : IEmbedderFactory
{
    public FakeEmbedder Embedder { get; set; } = embedder;

    public IEmbedder Create(EffectiveModelSettings settings) => Embedder;
}

internal sealed class FixedVectorStoreFactory(IVectorStore store) : IVectorStoreFactory
{
    public IVectorStore Create(EffectiveModelSettings settings) =>
        settings.VectorStore == VectorStoreKind.PgVector ? store : throw new NotSupportedException();
}

/// <summary>Reversible "encryption" so tests can check what was stored.</summary>
internal sealed class PlainSecretProtector : ISecretProtector
{
    public string Protect(string plaintext) => "enc:" + plaintext;
    public string Unprotect(string protectedValue) => protectedValue["enc:".Length..];
}
