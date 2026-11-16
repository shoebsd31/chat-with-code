using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ChatWithCode.AzureOpenAI;
using ChatWithCode.Core.Abstractions;
using ChatWithCode.Core.Domain;
using ChatWithCode.Tests.Infrastructure;

namespace ChatWithCode.Tests.Adapters;

public sealed class AzureOpenAIEmbedderTests
{
    private static readonly EffectiveModelSettings Settings = new()
    {
        Provider = ModelProviderKind.AzureOpenAI,
        AzureOpenAIEndpoint = new Uri("https://contoso.openai.azure.com/"),
        EmbeddingDeployment = "embed-small",
        EmbeddingDimensions = 4,
        ApiKey = "test-key",
        VectorStore = VectorStoreKind.PgVector,
    };

    [Fact]
    public async Task Sends_batches_to_the_deployment_and_returns_vectors_in_input_order()
    {
        var stub = new StubHttpHandler().On(r => r.RequestUri!.AbsolutePath == "/openai/deployments/embed-small/embeddings", EmbeddingResponse);
        var embedder = new AzureOpenAIEmbedderFactory(new HttpClient(stub)).Create(Settings);
        var inputs = Enumerable.Range(0, AzureOpenAIEmbedder.BatchSize + 6).Select(i => $"chunk {i}").ToList();

        var vectors = await embedder.EmbedAsync(inputs, CancellationToken.None);

        Assert.Equal(inputs.Count, vectors.Count);
        Assert.Equal(2, stub.Requests.Count); // 64 + 6
        Assert.Equal(70f, vectors[^1][0]);     // the stub encodes "chunk N" as N+1 in the first dimension
        Assert.Equal(1f, vectors[0][0]);

        var (request, body) = stub.Requests[0];
        Assert.Equal("test-key", request.Headers.GetValues("api-key").Single());
        using var json = JsonDocument.Parse(body!);
        Assert.Equal(4, json.RootElement.GetProperty("dimensions").GetInt32());
        Assert.Equal(AzureOpenAIEmbedder.BatchSize, json.RootElement.GetProperty("input").GetArrayLength());
        Assert.Equal("azure-openai:contoso.openai.azure.com/embed-small", embedder.ModelId);
    }

    [Fact]
    public async Task Wrong_dimension_count_is_reported()
    {
        var stub = new StubHttpHandler().On(_ => true, r => EmbeddingResponse(r, dimensions: 3));
        var embedder = new AzureOpenAIEmbedderFactory(new HttpClient(stub)).Create(Settings);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => embedder.EmbedAsync(["chunk 0"], CancellationToken.None));
        Assert.Contains("returned 3 dimensions", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_deployment_gives_a_clear_error()
    {
        var stub = new StubHttpHandler().On(_ => true, _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{"error":{"code":"DeploymentNotFound","message":"not found"}}""", Encoding.UTF8, "application/json"),
        });
        var embedder = new AzureOpenAIEmbedderFactory(new HttpClient(stub)).Create(Settings);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => embedder.EmbedAsync(["a"], CancellationToken.None));
        Assert.Contains("deployment", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns one vector per input, base64-encoded like the real service when the SDK asks for it.
    /// Items come back in reverse order to check re-ordering by index.
    /// </summary>
    private static HttpResponseMessage EmbeddingResponse(HttpRequestMessage request) => EmbeddingResponse(request, dimensions: 4);

    private static HttpResponseMessage EmbeddingResponse(HttpRequestMessage request, int dimensions)
    {
        using var json = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        var inputs = json.RootElement.GetProperty("input").EnumerateArray().Select(e => e.GetString()!).ToList();
        var base64 = json.RootElement.TryGetProperty("encoding_format", out var format) && format.GetString() == "base64";
        var data = inputs.Select((text, i) =>
        {
            var n = int.Parse(text.Split(' ').Last(), CultureInfo.InvariantCulture) + 1;
            var vector = Enumerable.Range(0, dimensions).Select(d => d == 0 ? (float)n : 0f).ToArray();
            object embedding = base64 ? Convert.ToBase64String(MemoryMarshal.AsBytes(vector.AsSpan())) : vector;
            return new { @object = "embedding", index = i, embedding };
        }).Reverse();
        var body = JsonSerializer.Serialize(new { @object = "list", data, model = "text-embedding-3-small", usage = new { prompt_tokens = 1, total_tokens = 1 } });
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
