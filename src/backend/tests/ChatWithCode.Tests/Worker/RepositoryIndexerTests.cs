using ChatWithCode.Core.Abstractions;
using ChatWithCode.Core.Domain;
using ChatWithCode.Core.Indexing;
using ChatWithCode.Core.Indexing.Chunking;
using ChatWithCode.Core.Options;
using ChatWithCode.Data.Entities;
using ChatWithCode.Data.Settings;
using ChatWithCode.Tests.Infrastructure;
using ChatWithCode.VectorStores.PgVector;
using ChatWithCode.Worker.Indexing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ChatWithCode.Tests.Worker;

[Collection(PostgresCollection.Name)]
public sealed class RepositoryIndexerTests(PostgresFixture postgres)
{
    private const string AppCs = """
        namespace Contoso.Orders;

        /// <summary>Submits orders.</summary>
        public sealed class OrderService
        {
            public bool Submit(int orderId) => orderId > 0;
        }
        """;

    private const string Readme = "# Orders\n\nHow to submit an order.\n";

    [Fact]
    public async Task Full_index_stores_chunks_for_source_files_only()
    {
        await using var h = await Harness.CreateAsync(postgres);
        var commit = h.Ado.Commit(new()
        {
            ["README.md"] = Readme,
            ["src/OrderService.cs"] = AppCs,
            ["web/node_modules/react/index.js"] = "module.exports = {}",
            ["package-lock.json"] = "{}",
            ["docs/huge.txt"] = new string('x', 3_000), // over the 2 KB test limit
        });
        h.Ado.CommitBytes(new()
        {
            ["README.md"] = "# Orders\n\nHow to submit an order.\n"u8.ToArray(),
            ["src/OrderService.cs"] = System.Text.Encoding.UTF8.GetBytes(AppCs),
            ["assets/data.bin.txt"] = [0x41, 0x00, 0x42], // binary content with a text-looking name
        });

        await h.RunAsync(IndexJobType.Full);

        await using var check = h.Db.NewContext();
        var chunks = await check.CodeChunks.ToListAsync();
        Assert.Equal(["README.md", "src/OrderService.cs"], chunks.Select(c => c.Path).Distinct().Order());
        Assert.All(chunks, c => Assert.Equal(h.Ado.Repository.Id, c.RepositoryId));

        var orderService = Assert.Single(chunks, c => c.Path == "src/OrderService.cs");
        Assert.Equal("Contoso.Orders.OrderService", orderService.Symbol);
        Assert.Equal("csharp", orderService.Language);
        Assert.Equal((1, 7), (orderService.StartLine, orderService.EndLine));

        var binary = await check.IndexedFiles.SingleAsync(f => f.Path == "assets/data.bin.txt");
        Assert.Equal(nameof(SkipReason.Binary), binary.SkipReason);
        Assert.DoesNotContain(h.Ado.DownloadedPaths, p => p.Contains("node_modules", StringComparison.Ordinal) || p.EndsWith("huge.txt", StringComparison.Ordinal));

        var branch = await check.IndexedBranches.SingleAsync();
        Assert.Equal(BranchIndexStatus.Ready, branch.Status);
        Assert.NotEqual(commit, branch.LastIndexedCommit); // indexed the second (latest) commit
        Assert.Equal((2, 2), (branch.FileCount, branch.ChunkCount));
        Assert.Equal("fake:embedding", branch.EmbeddingModel);
        Assert.Equal(1536, branch.EmbeddingDimensions);

        // The embedded text carries a header that helps retrieval.
        Assert.Contains(h.Embedder.Inputs, i => i.StartsWith("File: src/OrderService.cs\nLanguage: csharp\nSymbol: Contoso.Orders.OrderService\n", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Incremental_index_only_processes_changes()
    {
        await using var h = await Harness.CreateAsync(postgres);
        h.Ado.Commit(new()
        {
            ["README.md"] = Readme,
            ["src/OrderService.cs"] = AppCs,
            ["src/Unchanged.cs"] = "namespace Contoso; public class Unchanged { }",
        });
        await h.RunAsync(IndexJobType.Full);
        h.Ado.DownloadedPaths.Clear();
        h.Embedder.Inputs.Clear();

        h.Ado.Commit(new()
        {
            ["src/OrderService.cs"] = AppCs.Replace("orderId > 0", "orderId > 0 && orderId < 1000", StringComparison.Ordinal),
            ["src/Unchanged.cs"] = "namespace Contoso; public class Unchanged { }",
            ["docs/guide.md"] = "# Guide\n\nRefunds take 5 days.\n",
        });
        await h.RunAsync(IndexJobType.Incremental);

        Assert.Equal(["docs/guide.md", "src/OrderService.cs"], h.Ado.DownloadedPaths.Order());
        Assert.Equal(2, h.Embedder.Inputs.Count);

        await using var check = h.Db.NewContext();
        var paths = await check.CodeChunks.Select(c => c.Path).Distinct().OrderBy(p => p).ToListAsync();
        Assert.Equal(["docs/guide.md", "src/OrderService.cs", "src/Unchanged.cs"], paths);
        Assert.Contains(await check.CodeChunks.Select(c => c.Content).ToListAsync(), c => c.Contains("orderId < 1000", StringComparison.Ordinal));
        Assert.False(await check.IndexedFiles.AnyAsync(f => f.Path == "README.md"));
        Assert.Equal(3, (await check.IndexedBranches.SingleAsync()).FileCount);
    }

    [Fact]
    public async Task Same_commit_is_not_indexed_twice()
    {
        await using var h = await Harness.CreateAsync(postgres);
        h.Ado.Commit(new() { ["README.md"] = Readme });
        await h.RunAsync(IndexJobType.Incremental);
        h.Ado.DownloadedPaths.Clear();

        await h.RunAsync(IndexJobType.Incremental);

        Assert.Empty(h.Ado.DownloadedPaths);
        Assert.Equal(1, h.Embedder.Calls);
    }

    [Fact]
    public async Task Repo_ignore_file_excludes_and_re_includes_paths()
    {
        await using var h = await Harness.CreateAsync(postgres);
        h.Ado.Commit(new()
        {
            [".chatwithcodeignore"] = "# keep fixtures out\ntests/fixtures/\n!build/\n",
            ["tests/fixtures/orders.json"] = "[]",
            ["build/pipeline.yml"] = "trigger: [main]",
            ["src/OrderService.cs"] = AppCs,
        });

        await h.RunAsync(IndexJobType.Full);

        await using var check = h.Db.NewContext();
        var paths = (await check.CodeChunks.Select(c => c.Path).Distinct().ToListAsync()).Order(StringComparer.Ordinal);
        Assert.Equal([".chatwithcodeignore", "build/pipeline.yml", "src/OrderService.cs"], paths);
    }

    [Fact]
    public async Task Changing_the_embedding_model_reindexes_everything()
    {
        await using var h = await Harness.CreateAsync(postgres);
        h.Ado.Commit(new() { ["README.md"] = Readme, ["src/OrderService.cs"] = AppCs });
        await h.RunAsync(IndexJobType.Incremental);

        var newModel = new FakeEmbedder("fake:embedding-v2");
        h.Embedders.Embedder = newModel;
        await h.RunAsync(IndexJobType.Incremental); // same commit, but a new model

        Assert.Equal(2, newModel.Inputs.Count);
        Assert.Equal("fake:embedding-v2", (await h.Db.NewContext().IndexedBranches.SingleAsync()).EmbeddingModel);
    }

    [Fact]
    public async Task Failed_job_resumes_where_it_stopped()
    {
        await using var h = await Harness.CreateAsync(postgres);
        var files = Enumerable.Range(1, RepositoryIndexer.FilesPerBatch + 8)
            .ToDictionary(i => $"src/File{i:D2}.cs", i => $"namespace Contoso; public class File{i} {{ }}");
        h.Ado.Commit(files);
        h.Embedder.FailOnCall = 2; // the first batch succeeds, the second fails

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.RunAsync(IndexJobType.Incremental));
        Assert.Equal(RepositoryIndexer.FilesPerBatch, await h.Db.NewContext().IndexedFiles.CountAsync());

        h.Embedder.Inputs.Clear();
        await h.RunAsync(IndexJobType.Incremental); // retry

        Assert.Equal(8, h.Embedder.Inputs.Count);
        await using var check = h.Db.NewContext();
        Assert.Equal(files.Count, await check.CodeChunks.CountAsync());
        Assert.Equal(BranchIndexStatus.Ready, (await check.IndexedBranches.SingleAsync()).Status);
    }

    [Fact]
    public async Task Missing_settings_fail_with_a_clear_message()
    {
        await using var h = await Harness.CreateAsync(postgres, withSettings: false);
        h.Ado.Commit(new() { ["README.md"] = Readme });

        var error = await Assert.ThrowsAsync<SettingsIncompleteException>(() => h.RunAsync(IndexJobType.Full));
        Assert.Contains("Azure OpenAI endpoint", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Full_text_search_column_is_populated()
    {
        await using var h = await Harness.CreateAsync(postgres);
        h.Ado.Commit(new() { ["src/OrderService.cs"] = AppCs });
        await h.RunAsync(IndexJobType.Full);

        await using var check = h.Db.NewContext();
        var hits = await check.CodeChunks
            .Where(c => c.SearchVector!.Matches(EF.Functions.ToTsQuery("simple", "OrderService")))
            .CountAsync();
        Assert.Equal(1, hits);
    }

    [Fact]
    public void Chunk_ids_are_stable_and_distinct()
    {
        var target = new RepoBranch(Guid.Parse("11111111-1111-1111-1111-111111111111"), "refs/heads/main");
        Assert.Equal(RepositoryIndexer.ChunkId(target, "a.cs", 0), RepositoryIndexer.ChunkId(target, "a.cs", 0));
        Assert.NotEqual(RepositoryIndexer.ChunkId(target, "a.cs", 0), RepositoryIndexer.ChunkId(target, "a.cs", 1));
        Assert.Equal(32, RepositoryIndexer.ChunkId(target, "a.cs", 0).Length);
    }

    /// <summary>Database + fakes + a real indexer and pgvector store for one test.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private Harness(TestDb db) => Db = db;

        public TestDb Db { get; }
        public FakeAdoGitClient Ado { get; } = new();
        public FakeEmbedder Embedder { get; private set; } = new();
        public FakeEmbedderFactory Embedders { get; private set; } = null!;
        public Guid BranchId { get; private set; }

        public static async Task<Harness> CreateAsync(PostgresFixture postgres, bool withSettings = true)
        {
            var h = new Harness(await postgres.CreateMigratedDbAsync());
            h.Embedders = new FakeEmbedderFactory(h.Embedder);
            var db = h.Db.Context;

            var org = new Organization { Id = Guid.NewGuid(), Name = "contoso", CreatedAt = DateTimeOffset.UtcNow };
            var repo = new Repository
            {
                Id = Guid.NewGuid(),
                OrganizationId = org.Id,
                AdoRepositoryId = h.Ado.Repository.Id,
                AdoProjectId = h.Ado.Repository.ProjectId,
                ProjectName = h.Ado.Repository.ProjectName,
                Name = h.Ado.Repository.Name,
                DefaultBranch = "refs/heads/main",
                CreatedAt = DateTimeOffset.UtcNow,
            };
            var branch = new IndexedBranch { Id = Guid.NewGuid(), RepositoryId = repo.Id, BranchName = "refs/heads/main", IsDefault = true };
            db.AddRange(org, repo, branch);
            if (withSettings)
            {
                db.Add(new ModelSettings
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = org.Id,
                    Scope = SettingScope.Organization,
                    AzureOpenAIEndpoint = "https://contoso.openai.azure.com/",
                    EmbeddingDeployment = "text-embedding-3-small",
                    ApiKeyProtected = "enc:secret",
                    VectorStore = VectorStoreKind.PgVector,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
            }

            await db.SaveChangesAsync();
            h.BranchId = branch.Id;
            return h;
        }

        public async Task RunAsync(IndexJobType type)
        {
            await using var db = Db.NewContext();
            var tokens = new Cl100kTokenCounter();
            var indexer = new RepositoryIndexer(
                db,
                Ado,
                new ModelSettingsResolver(db, new PlainSecretProtector(), Options.Create(new AzureDevOpsOptions { Organization = "contoso" })),
                Embedders,
                new FixedVectorStoreFactory(new PgVectorStore(db)),
                tokens,
                new ChunkerSelector(tokens),
                Options.Create(new IndexingOptions { MaxFileSizeKb = 2 }),
                Db.Clock,
                NullLogger<RepositoryIndexer>.Instance);

            await indexer.HandleAsync(new IndexJobContext(Guid.NewGuid(), BranchId, type, 1, null), CancellationToken.None);
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
