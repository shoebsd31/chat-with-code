using System.Net;
using System.Text;
using System.Text.Json;
using ChatWithCode.AzureDevOps;
using ChatWithCode.AzureOpenAI;
using ChatWithCode.Core.Abstractions;
using ChatWithCode.Core.Domain;
using ChatWithCode.Tests.Infrastructure;

namespace ChatWithCode.Tests.Adapters;

public sealed class AdoUserClientTests
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");

    [Fact]
    public async Task Confirms_the_user_with_their_own_token()
    {
        var stub = new StubHttpHandler().On("/contoso/_apis/connectionData", HttpStatusCode.OK,
            $$$$"""{"authenticatedUser":{"id":"{{{{UserId}}}}","providerDisplayName":"Ada Lovelace","properties":{"Account":{"$type":"System.String","$value":"ada@contoso.com"}}},"instanceId":"x"}""");

        var user = await Client(stub).GetUserAsync("user-token", CancellationToken.None);

        Assert.Equal(new AdoUser(UserId.ToString(), "Ada Lovelace", "ada@contoso.com"), user);
        Assert.Equal("Bearer user-token", stub.Requests[0].Request.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task Lists_readable_repositories_and_skips_disabled_ones()
    {
        var repo = Guid.NewGuid();
        var stub = new StubHttpHandler().On("/contoso/_apis/git/repositories?api-version=7.1", HttpStatusCode.OK, $$$"""
            {"value":[
              {"id":"{{{repo}}}","name":"web-app","project":{"id":"{{{Guid.Empty}}}","name":"Fabrikam"}},
              {"id":"{{{Guid.NewGuid()}}}","name":"archived","isDisabled":true,"project":{"id":"{{{Guid.Empty}}}","name":"Fabrikam"}}
            ],"count":2}
            """);

        var repos = await Client(stub).GetReadableRepositoriesAsync("user-token", CancellationToken.None);

        Assert.Equal([new ReadableRepository(repo, "web-app", Guid.Empty, "Fabrikam")], repos);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NonAuthoritativeInformation)]
    public async Task Rejected_tokens_raise_access_errors(HttpStatusCode status)
    {
        var stub = new StubHttpHandler().On("/contoso/_apis/connectionData", status, "<html/>", "text/html");
        await Assert.ThrowsAsync<AdoAccessException>(() => Client(stub).GetUserAsync("expired", CancellationToken.None));
    }

    private static AdoUserClient Client(StubHttpHandler stub) =>
        new(new HttpClient(stub) { BaseAddress = new Uri("https://dev.azure.com/contoso/") });
}

public sealed class AzureOpenAIChatModelTests
{
    private static readonly EffectiveModelSettings Settings = new()
    {
        Provider = ModelProviderKind.AzureOpenAI,
        AzureOpenAIEndpoint = new Uri("https://contoso.openai.azure.com/"),
        ChatDeployment = "gpt-6-luna",
        EmbeddingDeployment = "embed",
        EmbeddingDimensions = 1536,
        ApiKey = "test-key",
        VectorStore = VectorStoreKind.PgVector,
    };

    [Fact]
    public async Task Streams_text_and_reports_token_usage()
    {
        var stub = new StubHttpHandler().On(r => r.RequestUri!.AbsolutePath == "/openai/deployments/gpt-6-luna/chat/completions", _ => Sse(
            """{"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"role":"assistant","content":"Order"}}]}""",
            """{"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"Service [1]"}}]}""",
            """{"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""",
            """{"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[],"usage":{"prompt_tokens":120,"completion_tokens":30,"total_tokens":150}}"""));
        var model = new AzureOpenAIChatModelFactory(new HttpClient(stub)).Create(Settings);

        var deltas = new List<ChatDelta>();
        await foreach (var d in model.StreamAsync(new ChatRequest("system rules", [new ChatMessage(MessageRole.User, "question")], 500), CancellationToken.None))
        {
            deltas.Add(d);
        }

        Assert.Equal("OrderService [1]", string.Concat(deltas.Select(d => d.Text)));
        Assert.Equal((120, 30), (deltas[^1].PromptTokens, deltas[^1].CompletionTokens));

        using var body = JsonDocument.Parse(stub.Requests[0].Body!);
        Assert.Equal("system", body.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal(500, body.RootElement.GetProperty("max_completion_tokens").GetInt32());
        Assert.False(body.RootElement.TryGetProperty("max_tokens", out _)); // reasoning models reject it
        Assert.False(body.RootElement.TryGetProperty("temperature", out _));
    }

    [Fact]
    public void Missing_chat_deployment_is_a_settings_problem()
    {
        var error = Assert.Throws<SettingsIncompleteException>(() => new AzureOpenAIChatModelFactory().Create(Settings with { ChatDeployment = null }));
        Assert.Contains("chat deployment", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Throttling_is_explained()
    {
        var stub = new StubHttpHandler().On(_ => true, _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("""{"error":{"code":"429","message":"Too many"}}""", Encoding.UTF8, "application/json"),
        });
        // No retries, so the test doesn't wait on backoff.
        var model = new AzureOpenAIChatModel(
            new Azure.AI.OpenAI.AzureOpenAIClient(Settings.AzureOpenAIEndpoint, new System.ClientModel.ApiKeyCredential("k"),
                new Azure.AI.OpenAI.AzureOpenAIClientOptions { Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(new HttpClient(stub)), RetryPolicy = new System.ClientModel.Primitives.ClientRetryPolicy(0) }),
            "gpt-6-luna");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in model.StreamAsync(new ChatRequest("s", [new ChatMessage(MessageRole.User, "q")]), CancellationToken.None))
            {
            }
        });
        Assert.Contains("rate limit", error.Message, StringComparison.Ordinal);
    }

    private static HttpResponseMessage Sse(params string[] chunks)
    {
        var body = string.Concat(chunks.Select(c => $"data: {c}\n\n")) + "data: [DONE]\n\n";
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
    }
}
