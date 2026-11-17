using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using ChatWithCode.Api;
using ChatWithCode.Api.Auth;
using ChatWithCode.Api.Chat;
using ChatWithCode.Core.Abstractions;
using ChatWithCode.Core.Chat;
using ChatWithCode.Core.Domain;
using ChatWithCode.Data.Entities;
using ChatWithCode.Tests.Infrastructure;
using ChatWithCode.VectorStores.PgVector;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ChatWithCode.Tests.Api;

[Collection(PostgresCollection.Name)]
public sealed class ChatApiTests(PostgresFixture postgres)
{
    private const string Secret = "test-extension-secret-that-is-long-enough-for-hs256";
    private static readonly Guid ProjectId = Guid.NewGuid();
    private static readonly Guid WebAppId = Guid.NewGuid();
    private static readonly Guid SecretRepoId = Guid.NewGuid();
    private static readonly Guid NotIndexedId = Guid.NewGuid();

    [Fact]
    public async Task Requests_need_a_valid_extension_token()
    {
        await using var app = await ChatApp.CreateAsync(postgres);

        using var noToken = app.Client(appToken: null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await noToken.GetAsync(Url("/api/me"))).StatusCode);

        using var wrongSecret = app.Client(appToken: AppToken("another-secret-that-is-long-enough-for-hs256-keys"));
        var response = await wrongSecret.GetAsync(Url("/api/me"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("didn't come from the Chat with Code extension", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var expired = app.Client(appToken: AppToken(Secret, expires: DateTime.UtcNow.AddMinutes(-10)));
        Assert.Contains("session expired", await (await expired.GetAsync(Url("/api/me"))).Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Extension_secret_copied_with_stray_spaces_still_works()
    {
        await using var app = await ChatApp.CreateAsync(postgres, secret: $"  {Secret}\n");

        using var client = app.Client();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Url("/api/me"))).StatusCode);
    }

    [Fact]
    public async Task Too_short_extension_secret_is_explained()
    {
        await using var app = await ChatApp.CreateAsync(postgres, secret: "14-characters!");

        using var client = app.Client();
        var response = await client.GetAsync(Url("/api/me"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("too short to be the extension's key", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Requests_need_an_Azure_DevOps_sign_in_that_Azure_DevOps_accepts()
    {
        await using var app = await ChatApp.CreateAsync(postgres);

        using var missing = app.Client(userToken: null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await missing.GetAsync(Url("/api/me"))).StatusCode);

        using var rejected = app.Client(userToken: "revoked");
        var response = await rejected.GetAsync(Url("/api/me"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("didn't accept your sign-in", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var ok = app.Client();
        Assert.Equal("Ada Lovelace", (await ok.GetFromJsonAsync<MeDto>(Url("/api/me")))!.DisplayName);
    }

    [Fact]
    public async Task Repository_list_only_shows_indexed_repositories_the_user_can_read()
    {
        await using var app = await ChatApp.CreateAsync(postgres);
        using var client = app.Client();

        var repos = (await client.GetFromJsonAsync<List<RepositoryDto>>(Url("/api/repositories")))!;

        Assert.Equal(["not-indexed", "web-app"], repos.Select(r => r.Name));
        var webApp = repos.Single(r => r.Name == "web-app");
        Assert.Equal(("refs/heads/main", true, "Ready"), (webApp.Branches[0].Name, webApp.Branches[0].IsDefault, webApp.Branches[0].Status));
    }

    [Fact]
    public async Task Chat_streams_an_answer_with_citations_and_saves_it()
    {
        await using var app = await ChatApp.CreateAsync(postgres);
        app.Model.Answer = ["`OrderService.Submit` accepts ", "positive order ids [1]."];
        using var client = app.Client();

        var events = await AskAsync(client, new AskRequest("How does OrderService submit orders?", Targets: [new ChatTarget(WebAppId, "refs/heads/main")]));

        Assert.Equal(["conversation", "delta", "delta", "citations", "done"], events.Select(e => e.Type));
        var citations = events.Single(e => e.Type == "citations").Data;
        Assert.True(citations.GetProperty("answerFound").GetBoolean());
        var first = citations.GetProperty("citations")[0];
        Assert.Equal("src/OrderService.cs", first.GetProperty("path").GetString());
        Assert.True(first.GetProperty("used").GetBoolean());
        Assert.StartsWith("https://dev.azure.com/contoso/Fabrikam/_git/web-app?path=%2Fsrc%2FOrderService.cs&version=GBmain&line=1", first.GetProperty("url").GetString(), StringComparison.Ordinal);

        // The model saw the source as data and the question last.
        var prompt = app.Model.Requests.Single().Messages[^1].Content;
        Assert.Contains("path=\"src/OrderService.cs\"", prompt, StringComparison.Ordinal);
        Assert.EndsWith("Question: How does OrderService submit orders?", prompt, StringComparison.Ordinal);

        // Saved with both messages, the citation and token usage.
        var conversationId = events[0].Data.GetProperty("conversationId").GetGuid();
        var saved = await client.GetFromJsonAsync<ConversationDto>(Url($"/api/conversations/{conversationId}"));
        Assert.Equal("How does OrderService submit orders?", saved!.Title);
        Assert.Equal(["User", "Assistant"], saved.Messages.Select(m => m.Role));
        Assert.Equal("`OrderService.Submit` accepts positive order ids [1].", saved.Messages[1].Content);
        Assert.Equal("src/OrderService.cs", Assert.Single(saved.Messages[1].Citations).Path);
        await using var db = app.Db.NewContext();
        var answer = await db.Messages.SingleAsync(m => m.Role == MessageRole.Assistant);
        Assert.Equal((120, 30, "gpt-6-luna"), (answer.PromptTokens, answer.CompletionTokens, answer.Model));
    }

    [Fact]
    public async Task Follow_up_questions_are_rewritten_for_search_and_keep_history()
    {
        await using var app = await ChatApp.CreateAsync(postgres);
        using var client = app.Client();
        var first = await AskAsync(client, new AskRequest("What does OrderService do?", Targets: [new ChatTarget(WebAppId, "refs/heads/main")]));
        var conversationId = first[0].Data.GetProperty("conversationId").GetGuid();
        app.Model.Requests.Clear();

        var events = await AskAsync(client, new AskRequest("and where is it tested?", conversationId));

        Assert.Equal("done", events[^1].Type);
        Assert.Equal(PromptBuilder.RewriteSystemPrompt, app.Model.Requests[0].SystemPrompt); // rewrite first…
        var answer = app.Model.Requests[1];                                                  // …then the answer with history
        Assert.Equal([MessageRole.User, MessageRole.Assistant, MessageRole.User], answer.Messages.Select(m => m.Role));
        Assert.Equal(4, (await client.GetFromJsonAsync<ConversationDto>(Url($"/api/conversations/{conversationId}")))!.Messages.Count);
    }

    [Fact]
    public async Task Unanswerable_question_says_so_and_lists_closest_files()
    {
        await using var app = await ChatApp.CreateAsync(postgres);
        app.Model.Answer = [PromptBuilder.NotFoundAnswer + " The closest source covers order submission."];
        using var client = app.Client();

        var events = await AskAsync(client, new AskRequest("How is payroll calculated?", Targets: [new ChatTarget(WebAppId, "refs/heads/main")]));

        var citations = events.Single(e => e.Type == "citations").Data;
        Assert.False(citations.GetProperty("answerFound").GetBoolean());
        Assert.All(citations.GetProperty("citations").EnumerateArray(), c => Assert.False(c.GetProperty("used").GetBoolean()));
        Assert.NotEmpty(citations.GetProperty("citations").EnumerateArray());
    }

    [Theory]
    [InlineData("secret", HttpStatusCode.Forbidden, "don't have access")]
    [InlineData("not-indexed", HttpStatusCode.Conflict, "isn't indexed yet")]
    [InlineData("empty-question", HttpStatusCode.BadRequest, "Type a question")]
    public async Task Invalid_requests_get_a_clear_problem_response(string scenario, HttpStatusCode status, string message)
    {
        await using var app = await ChatApp.CreateAsync(postgres);
        using var client = app.Client();
        var request = scenario switch
        {
            "secret" => new AskRequest("What is in here?", Targets: [new ChatTarget(SecretRepoId, "refs/heads/main")]),
            "not-indexed" => new AskRequest("What is in here?", Targets: [new ChatTarget(NotIndexedId, "refs/heads/main")]),
            _ => new AskRequest("   ", Targets: [new ChatTarget(WebAppId, "refs/heads/main")]),
        };

        var response = await client.PostAsJsonAsync(Url("/api/chat"), request);

        Assert.Equal(status, response.StatusCode);
        Assert.Contains(message, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Empty(app.Model.Requests);
    }

    [Fact]
    public async Task Model_errors_end_the_stream_with_a_readable_message()
    {
        await using var app = await ChatApp.CreateAsync(postgres);
        app.Model.FailWith = "Azure OpenAI is busy (rate limit reached). Try again in a minute.";
        using var client = app.Client();

        var events = await AskAsync(client, new AskRequest("How does OrderService submit orders?", Targets: [new ChatTarget(WebAppId, "refs/heads/main")]));

        Assert.Equal("error", events[^1].Type);
        Assert.Equal(app.Model.FailWith, events[^1].Data.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Users_only_see_and_delete_their_own_conversations()
    {
        await using var app = await ChatApp.CreateAsync(postgres);
        using var ada = app.Client();
        var events = await AskAsync(ada, new AskRequest("What does OrderService do?", Targets: [new ChatTarget(WebAppId, "refs/heads/main")]));
        var id = events[0].Data.GetProperty("conversationId").GetGuid();

        using var grace = app.Client(userToken: "grace-token");
        Assert.Empty((await grace.GetFromJsonAsync<List<ConversationSummaryDto>>(Url("/api/conversations")))!);
        Assert.Equal(HttpStatusCode.NotFound, (await grace.GetAsync(Url($"/api/conversations/{id}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await grace.DeleteAsync(Url($"/api/conversations/{id}"))).StatusCode);

        Assert.Single((await ada.GetFromJsonAsync<List<ConversationSummaryDto>>(Url("/api/conversations")))!);
        Assert.Equal(HttpStatusCode.NoContent, (await ada.DeleteAsync(Url($"/api/conversations/{id}"))).StatusCode);
        Assert.Empty((await ada.GetFromJsonAsync<List<ConversationSummaryDto>>(Url("/api/conversations")))!);
    }

    [Fact]
    public async Task Too_many_questions_are_rate_limited()
    {
        await using var app = await ChatApp.CreateAsync(postgres, requestsPerMinute: 2);
        using var client = app.Client();
        var request = new AskRequest("   ", Targets: [new ChatTarget(WebAppId, "refs/heads/main")]); // cheap: rejected before the model

        await client.PostAsJsonAsync(Url("/api/chat"), request);
        await client.PostAsJsonAsync(Url("/api/chat"), request);
        var third = await client.PostAsJsonAsync(Url("/api/chat"), request);

        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.Contains("questions in the last minute", await third.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Info_reports_whether_the_extension_is_connected()
    {
        await using var app = await ChatApp.CreateAsync(postgres);
        using var anonymous = app.Client(appToken: null, userToken: null);

        var info = await anonymous.GetFromJsonAsync<ServiceInfo>(Url("/api/info"));

        Assert.True(info!.ExtensionConnected);
    }

    private static Uri Url(string path) => new(path, UriKind.Relative);

    private static string AppToken(string secret, DateTime? expires = null)
    {
        var expiry = expires ?? DateTime.UtcNow.AddMinutes(30);
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Expires = expiry,
            NotBefore = expiry.AddHours(-1),
            IssuedAt = expiry.AddHours(-1),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)), SecurityAlgorithms.HmacSha256),
        });
    }

    private static async Task<List<(string Type, JsonElement Data)>> AskAsync(HttpClient client, AskRequest request)
    {
        var response = await client.PostAsJsonAsync(Url("/api/chat"), request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);

        var events = new List<(string, JsonElement)>();
        string? type = null;
        foreach (var line in (await response.Content.ReadAsStringAsync()).Split('\n'))
        {
            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                type = line["event: ".Length..].Trim();
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                events.Add((type!, JsonDocument.Parse(line["data: ".Length..]).RootElement.Clone()));
            }
        }

        return events;
    }

    /// <summary>The real API with fake Azure DevOps and Azure OpenAI, on a migrated test database.</summary>
    private sealed class ChatApp : IAsyncDisposable
    {
        private readonly WebApplicationFactory<ServiceInfo> _factory;

        private ChatApp(WebApplicationFactory<ServiceInfo> factory, TestDb db, FakeChatModel model)
        {
            _factory = factory;
            Db = db;
            Model = model;
        }

        public TestDb Db { get; }
        public FakeChatModel Model { get; }

        public static async Task<ChatApp> CreateAsync(PostgresFixture postgres, int requestsPerMinute = 100, string secret = Secret)
        {
            var connectionString = await postgres.CreateDatabaseAsync();
            var model = new FakeChatModel();
            var embedder = new FakeEmbedder();
            var factory = new WebApplicationFactory<ServiceInfo>().WithWebHostBuilder(b => b
                .UseSetting("ConnectionStrings:Metadata", connectionString)
                .UseSetting("AzureDevOps:Organization", "contoso")
                .UseSetting("AzureDevOps:ExtensionSecret", secret)
                .UseSetting("AzureOpenAI:Endpoint", "https://contoso.openai.azure.com/")
                .UseSetting("AzureOpenAI:ApiKey", "key")
                .UseSetting("AzureOpenAI:EmbeddingDeployment", "text-embedding-3-small")
                .UseSetting("AzureOpenAI:ChatDeployment", "gpt-6-luna")
                .UseSetting("Chat:RequestsPerMinute", requestsPerMinute.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .UseSetting("Security:DataProtectionKeysPath", Directory.CreateTempSubdirectory("cwc-keys").FullName)
                .ConfigureTestServices(services =>
                {
                    services.AddSingleton<IAdoUserClient>(new FakeAdoUserClient());
                    services.AddSingleton<IEmbedderFactory>(new FakeEmbedderFactory(embedder));
                    services.AddSingleton<IChatModelFactory>(new FakeChatModelFactory(model));
                }));

            factory.CreateClient().Dispose(); // start: migrations + organization + settings from configuration
            var app = new ChatApp(factory, TestDb.Create(connectionString), model);
            await app.SeedAsync(embedder);
            return app;
        }

        public HttpClient Client(string? appToken = "valid", string? userToken = "ada-token")
        {
            var client = _factory.CreateClient();
            if (appToken is not null)
            {
                client.DefaultRequestHeaders.Authorization = new("Bearer", appToken == "valid" ? AppToken(Secret) : appToken);
            }

            if (userToken is not null)
            {
                client.DefaultRequestHeaders.Add(RequireUserAccessFilter.HeaderName, userToken);
            }

            return client;
        }

        private async Task SeedAsync(FakeEmbedder embedder)
        {
            await using var db = Db.NewContext();
            var orgId = await db.Organizations.Select(o => o.Id).SingleAsync();
            Repository Repo(Guid id, string name, BranchIndexStatus status, int chunks) => new()
            {
                Id = Guid.NewGuid(), OrganizationId = orgId, AdoRepositoryId = id, AdoProjectId = ProjectId, ProjectName = "Fabrikam",
                Name = name, DefaultBranch = "refs/heads/main", CreatedAt = DateTimeOffset.UtcNow,
                Branches = [new IndexedBranch { Id = Guid.NewGuid(), BranchName = "refs/heads/main", IsDefault = true, Status = status, ChunkCount = chunks }],
            };
            db.Repositories.AddRange(
                Repo(WebAppId, "web-app", BranchIndexStatus.Ready, 2),
                Repo(SecretRepoId, "secret", BranchIndexStatus.Ready, 1),
                Repo(NotIndexedId, "not-indexed", BranchIndexStatus.Queued, 0));
            await db.SaveChangesAsync();

            var main = new RepoBranch(WebAppId, "refs/heads/main");
            string[] contents =
            [
                "public sealed class OrderService\n{\n    public bool Submit(int orderId) => orderId > 0;\n}",
                "# Web app\n\nThe web app sells things.",
            ];
            var vectors = await embedder.EmbedAsync(contents, CancellationToken.None);
            await new PgVectorStore(db).UpsertAsync(
            [
                new CodeChunk("c1", main, "src/OrderService.cs", "csharp", 1, 4, "OrderService", contents[0], "abc", vectors[0]),
                new CodeChunk("c2", main, "README.md", "markdown", 1, 3, "Web app", contents[1], "abc", vectors[1]),
            ], CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _factory.DisposeAsync();
        }
    }

    private sealed class FakeAdoUserClient : IAdoUserClient
    {
        public Task<AdoUser> GetUserAsync(string userAccessToken, CancellationToken cancellationToken) => userAccessToken switch
        {
            "ada-token" => Task.FromResult(new AdoUser("user-ada", "Ada Lovelace")),
            "grace-token" => Task.FromResult(new AdoUser("user-grace", "Grace Hopper")),
            _ => throw new AdoAccessException("Azure DevOps didn't accept your sign-in for this organization. Reload the page and try again."),
        };

        // Both users can read web-app and not-indexed, but not "secret".
        public Task<IReadOnlyList<ReadableRepository>> GetReadableRepositoriesAsync(string userAccessToken, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ReadableRepository>>(
            [
                new ReadableRepository(WebAppId, "web-app", ProjectId, "Fabrikam"),
                new ReadableRepository(NotIndexedId, "not-indexed", ProjectId, "Fabrikam"),
            ]);
    }

    private sealed class FakeChatModelFactory(FakeChatModel model) : IChatModelFactory
    {
        public IChatModel Create(EffectiveModelSettings settings)
        {
            Assert.Equal("gpt-6-luna", settings.ChatDeployment);
            return model;
        }
    }

    internal sealed class FakeChatModel : IChatModel
    {
        public List<ChatRequest> Requests { get; } = [];
        public string[] Answer { get; set; } = ["OrderService submits orders [1]."];
        public string? FailWith { get; set; }

        public async IAsyncEnumerable<ChatDelta> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (FailWith is not null)
            {
                throw new InvalidOperationException(FailWith);
            }

            if (request.SystemPrompt == PromptBuilder.RewriteSystemPrompt)
            {
                yield return new ChatDelta("Where is OrderService tested?");
                yield break;
            }

            foreach (var part in Answer[..^1])
            {
                await Task.Yield();
                yield return new ChatDelta(part);
            }

            yield return new ChatDelta(Answer[^1], PromptTokens: 120, CompletionTokens: 30);
        }
    }
}
