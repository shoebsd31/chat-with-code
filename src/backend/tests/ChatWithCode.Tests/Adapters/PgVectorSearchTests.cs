using ChatWithCode.Core.Abstractions;
using ChatWithCode.Tests.Infrastructure;
using ChatWithCode.VectorStores.PgVector;

namespace ChatWithCode.Tests.Adapters;

[Collection(PostgresCollection.Name)]
public sealed class PgVectorSearchTests(PostgresFixture postgres)
{
    private static readonly RepoBranch Main = new(Guid.NewGuid(), "refs/heads/main");
    private static readonly RepoBranch Other = new(Guid.NewGuid(), "refs/heads/main");

    [Fact]
    public async Task Exact_identifier_ranks_first_and_other_branches_are_excluded()
    {
        await using var t = await postgres.CreateMigratedDbAsync();
        var embedder = new FakeEmbedder();
        var store = new PgVectorStore(t.Context);
        var chunks = new (RepoBranch Target, string Path, string Content)[]
        {
            (Main, "src/RefundService.cs", "public class RefundService { public void Refund(int orderId) { } }"),
            (Main, "src/OrderService.cs", "public class OrderService { public bool Submit(int orderId) => orderId > 0; }"),
            (Main, "README.md", "# Shop\nThe shop sells things."),
            (Other, "src/RefundService.cs", "public class RefundService { /* secret repo */ }"),
        };
        var vectors = await embedder.EmbedAsync(chunks.Select(c => c.Content).ToList(), CancellationToken.None);
        await store.UpsertAsync(chunks.Select((c, i) => new CodeChunk($"c{i}", c.Target, c.Path, "csharp", 1, 1, null, c.Content, "abc", vectors[i])).ToList(), CancellationToken.None);

        var question = "How are refunds handled by RefundService?";
        var queryVector = (await embedder.EmbedAsync([question], CancellationToken.None))[0];
        var hits = await store.HybridSearchAsync(new SearchQuery(question, queryVector, [Main], Top: 10), CancellationToken.None);

        Assert.Equal("src/RefundService.cs", hits[0].Chunk.Path);
        Assert.All(hits, h => Assert.Equal(Main, h.Chunk.Target));
        Assert.Equal(3, hits.Count); // vector search returns every allowed chunk; keyword search only boosts
        Assert.True(hits[0].Score > hits[1].Score);
        Assert.Contains("Refund(int orderId)", hits[0].Chunk.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_without_keywords_uses_vectors_only()
    {
        await using var t = await postgres.CreateMigratedDbAsync();
        var embedder = new FakeEmbedder();
        var store = new PgVectorStore(t.Context);
        var vector = (await embedder.EmbedAsync(["a"], CancellationToken.None))[0];
        await store.UpsertAsync([new CodeChunk("c1", Main, "a.txt", "text", 1, 1, null, "a", "abc", vector)], CancellationToken.None);

        var hits = await store.HybridSearchAsync(new SearchQuery("how does it work?", vector, [Main], Top: 5), CancellationToken.None);

        Assert.Single(hits);
    }

    [Fact]
    public async Task No_allowed_targets_returns_nothing()
    {
        await using var t = await postgres.CreateMigratedDbAsync();
        Assert.Empty(await new PgVectorStore(t.Context).HybridSearchAsync(new SearchQuery("x", new float[1536], [], 5), CancellationToken.None));
    }
}
