using System.Net;
using System.Text;
using System.Text.Json;
using ChatWithCode.Core.Abstractions;
using ChatWithCode.Core.Domain;
using ChatWithCode.Tests.Infrastructure;
using ChatWithCode.VectorStores.AzureAISearch;

namespace ChatWithCode.Tests.Adapters;

public sealed class AzureAISearchStoreTests
{
    private static readonly RepoBranch Main = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "refs/heads/main");

    private static readonly EffectiveModelSettings Settings = new()
    {
        Provider = ModelProviderKind.AzureOpenAI,
        AzureOpenAIEndpoint = new Uri("https://contoso.openai.azure.com/"),
        EmbeddingDeployment = "embed",
        EmbeddingDimensions = 3072,
        ApiKey = "k",
        VectorStore = VectorStoreKind.AzureAISearch,
        AzureSearchEndpoint = new Uri("https://contoso-search.search.windows.net/"),
        AzureSearchIndexName = "code-chunks",
        AzureSearchApiKey = "search-admin-key",
    };

    [Fact]
    public async Task Creates_a_missing_index_with_vector_settings()
    {
        var stub = new StubHttpHandler()
            .On(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.Contains("indexes('code-chunks')", StringComparison.Ordinal), _ => Json(HttpStatusCode.NotFound, """{"error":{"code":"ResourceNotFound","message":"No index"}}"""))
            .On(r => r.Method == HttpMethod.Put, r => Json(HttpStatusCode.Created, r.Content!.ReadAsStringAsync().Result));

        await Store(stub).EnsureIndexAsync(new IndexSpec("m", 3072), CancellationToken.None);

        var (put, body) = stub.Requests.Single(r => r.Request.Method == HttpMethod.Put);
        Assert.Equal("search-admin-key", put.Headers.GetValues("api-key").Single());
        using var index = JsonDocument.Parse(body!);
        var fields = index.RootElement.GetProperty("fields").EnumerateArray().ToDictionary(f => f.GetProperty("name").GetString()!);
        Assert.True(fields["id"].GetProperty("key").GetBoolean());
        Assert.True(fields["target"].GetProperty("filterable").GetBoolean());
        Assert.Equal(3072, fields["embedding"].GetProperty("dimensions").GetInt32());
        Assert.Equal("cosine", index.RootElement.GetProperty("vectorSearch").GetProperty("algorithms")[0].GetProperty("hnswParameters").GetProperty("metric").GetString());
    }

    [Fact]
    public async Task Existing_index_with_another_vector_size_is_refused()
    {
        var stub = new StubHttpHandler().On(r => r.Method == HttpMethod.Get, _ => Json(HttpStatusCode.OK, """
            {"name":"code-chunks","fields":[{"name":"id","type":"Edm.String","key":true},{"name":"embedding","type":"Collection(Edm.Single)","dimensions":1536,"vectorSearchProfile":"p"}]}
            """));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Store(stub).EnsureIndexAsync(new IndexSpec("m", 3072), CancellationToken.None));
        Assert.Contains("use a new index name", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uploads_chunks_with_a_filter_key_per_branch()
    {
        var stub = new StubHttpHandler().On(r => r.RequestUri!.AbsolutePath.EndsWith("/docs/search.index", StringComparison.Ordinal), r =>
        {
            using var body = JsonDocument.Parse(r.Content!.ReadAsStringAsync().Result);
            var results = body.RootElement.GetProperty("value").EnumerateArray().Select(d => new { key = d.GetProperty("id").GetString(), status = true, statusCode = 201 });
            return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { value = results }));
        });

        await Store(stub).UpsertAsync([new CodeChunk("c1", Main, "src/A.cs", "csharp", 1, 9, "A", "class A {}", "abc", [0.5f, 0.25f])], CancellationToken.None);

        using var doc = JsonDocument.Parse(stub.Requests.Single().Body!);
        var item = doc.RootElement.GetProperty("value")[0];
        Assert.Equal("mergeOrUpload", item.GetProperty("@search.action").GetString());
        Assert.Equal(AzureAISearchStore.TargetKey(Main), item.GetProperty("target").GetString());
        Assert.Equal("refs/heads/main", item.GetProperty("branch").GetString());
        Assert.Equal(2, item.GetProperty("embedding").GetArrayLength());
    }

    [Fact]
    public async Task Hybrid_search_sends_terms_vector_and_branch_filter()
    {
        var stub = new StubHttpHandler().On(r => r.RequestUri!.AbsolutePath.EndsWith("/docs/search.post.search", StringComparison.Ordinal), _ => Json(HttpStatusCode.OK, $$"""
            {"value":[{"@search.score":0.033,"id":"c1","repositoryId":"{{Main.RepositoryId}}","branch":"refs/heads/main","path":"src/RefundService.cs","language":"csharp","startLine":3,"endLine":20,"symbol":"RefundService","content":"class RefundService {}","commitId":"abc"}]}
            """));

        var hits = await Store(stub).HybridSearchAsync(new SearchQuery("How does the RefundService work?", [0.1f, 0.2f], [Main], Top: 12), CancellationToken.None);

        var hit = Assert.Single(hits);
        Assert.Equal(("src/RefundService.cs", 3, 20, "RefundService"), (hit.Chunk.Path, hit.Chunk.StartLine, hit.Chunk.EndLine, hit.Chunk.Symbol));
        Assert.Equal(Main, hit.Chunk.Target);
        Assert.Equal(0.033, hit.Score, 3);

        using var request = JsonDocument.Parse(stub.Requests.Single().Body!);
        var root = request.RootElement;
        Assert.Equal("refundservice", root.GetProperty("search").GetString());
        Assert.Equal(AzureAISearchStore.AllowedFilter([Main]), root.GetProperty("filter").GetString());
        Assert.Equal("preFilter", root.GetProperty("vectorFilterMode").GetString());
        var vector = root.GetProperty("vectorQueries")[0];
        Assert.Equal("embedding", vector.GetProperty("fields").GetString());
        Assert.Equal(40, vector.GetProperty("k").GetInt32());
        Assert.Equal(12, root.GetProperty("top").GetInt32());
    }

    [Fact]
    public async Task Deleting_a_branch_finds_every_document_then_deletes_them()
    {
        var stub = new StubHttpHandler()
            .On(r => r.RequestUri!.AbsolutePath.EndsWith("/docs/search.post.search", StringComparison.Ordinal), _ => Json(HttpStatusCode.OK, """{"value":[{"id":"c1"},{"id":"c2"}]}"""))
            .On(r => r.RequestUri!.AbsolutePath.EndsWith("/docs/search.index", StringComparison.Ordinal), _ => Json(HttpStatusCode.OK, """{"value":[{"key":"c1","status":true,"statusCode":200},{"key":"c2","status":true,"statusCode":200}]}"""));

        await Store(stub).DeleteBranchAsync(Main, CancellationToken.None);

        using var search = JsonDocument.Parse(stub.Requests[0].Body!);
        Assert.Equal($"target eq '{AzureAISearchStore.TargetKey(Main)}'", search.RootElement.GetProperty("filter").GetString());
        using var delete = JsonDocument.Parse(stub.Requests[1].Body!);
        Assert.All(delete.RootElement.GetProperty("value").EnumerateArray(), d => Assert.Equal("delete", d.GetProperty("@search.action").GetString()));
        Assert.Equal(2, delete.RootElement.GetProperty("value").GetArrayLength());
    }

    [Fact]
    public async Task Deleting_files_escapes_quotes_in_paths()
    {
        var stub = new StubHttpHandler().On(_ => true, _ => Json(HttpStatusCode.OK, """{"value":[]}"""));

        await Store(stub).DeleteFilesAsync(Main, ["docs/it's.md"], CancellationToken.None);

        using var search = JsonDocument.Parse(stub.Requests[0].Body!);
        Assert.Contains("path eq 'docs/it''s.md'", search.RootElement.GetProperty("filter").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Target_keys_are_filter_safe_and_distinct()
    {
        var odd = new RepoBranch(Main.RepositoryId, "refs/heads/feature/it's,a|test");
        Assert.Matches("^[0-9a-f]{32}-[0-9a-f]{16}$", AzureAISearchStore.TargetKey(odd));
        Assert.NotEqual(AzureAISearchStore.TargetKey(Main), AzureAISearchStore.TargetKey(odd));
    }

    [Fact]
    public async Task Rejected_key_explains_the_roles_needed()
    {
        var stub = new StubHttpHandler().On(_ => true, _ => Json(HttpStatusCode.Forbidden, """{"error":{"code":"Forbidden","message":"no"}}"""));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Store(stub).HybridSearchAsync(new SearchQuery("x", [0f], [Main], 5), CancellationToken.None));
        Assert.Contains("Search Index Data Contributor", error.Message, StringComparison.Ordinal);
    }

    private static IVectorStore Store(StubHttpHandler stub) => new AzureAISearchStoreFactory(new HttpClient(stub)).Create(Settings);

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
