using System.Net;
using System.Net.Http.Json;
using System.Text;
using ChatWithCode.Api;
using ChatWithCode.Api.Admin;
using ChatWithCode.Api.Auth;
using ChatWithCode.Api.Chat;
using ChatWithCode.Core.Abstractions;
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
public sealed class AdminApiTests(PostgresFixture postgres)
{
    private const string Secret = "test-extension-secret-that-is-long-enough-for-hs256";

    [Fact]
    public async Task Me_reports_whether_the_user_is_an_admin()
    {
        await using var app = await AdminApp.CreateAsync(postgres);

        Assert.True((await app.Admin.GetFromJsonAsync<MeDto>(Url("/api/me")))!.IsAdmin);
        Assert.False((await app.Member.GetFromJsonAsync<MeDto>(Url("/api/me")))!.IsAdmin);
    }

    [Fact]
    public async Task Members_can_read_settings_but_not_change_them()
    {
        await using var app = await AdminApp.CreateAsync(postgres);

        var settings = await app.Member.GetFromJsonAsync<SettingsResponse>(Url("/api/admin/settings"));
        Assert.False(settings!.CanEdit);
        Assert.Contains("Missing", settings.Problem, StringComparison.Ordinal);

        var response = await app.Member.PutAsJsonAsync(Url("/api/admin/settings"), OrgSettings("key-123456789"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("CHAT_ADMINS", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Admin_saves_settings_and_keys_are_never_returned()
    {
        await using var app = await AdminApp.CreateAsync(postgres);

        var saved = await (await app.Admin.PutAsJsonAsync(Url("/api/admin/settings"), OrgSettings("sk-secret-value-ab12"))).Content.ReadFromJsonAsync<SaveSettingsResponse>();
        Assert.Equal(new SecretState(true, "…ab12"), saved!.Settings.Organization.ApiKey);
        Assert.Null(saved.Settings.Problem);
        Assert.Equal("ada@contoso.com", saved.Settings.Organization.UpdatedBy);

        // Saving again without a key keeps the stored one; an empty key removes it.
        await app.Admin.PutAsJsonAsync(Url("/api/admin/settings"), OrgSettings(apiKey: null) with { ChatDeployment = "gpt-6.1-terra" });
        var kept = await app.Admin.GetFromJsonAsync<SettingsResponse>(Url("/api/admin/settings"));
        Assert.Equal(("gpt-6.1-terra", true), (kept!.Organization.ChatDeployment, kept.Organization.ApiKey.IsSet));

        await app.Admin.PutAsJsonAsync(Url("/api/admin/settings"), OrgSettings(apiKey: ""));
        var cleared = await app.Admin.GetFromJsonAsync<SettingsResponse>(Url("/api/admin/settings"));
        Assert.False(cleared!.Organization.ApiKey.IsSet);
        Assert.Contains("API key", cleared.Problem, StringComparison.Ordinal);

        var raw = await app.Admin.GetStringAsync(Url("/api/admin/settings"));
        Assert.DoesNotContain("sk-secret-value", raw, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://contoso.openai.azure.com/", 1536, "https://")]
    [InlineData("https://contoso.openai.azure.com/", 3072, "1536 dimensions")]
    [InlineData("https://contoso.openai.azure.com/", 99, "between 256 and 3072")]
    public async Task Invalid_settings_are_explained(string endpoint, int dimensions, string message)
    {
        await using var app = await AdminApp.CreateAsync(postgres);

        var response = await app.Admin.PutAsJsonAsync(Url("/api/admin/settings"), OrgSettings("k") with { AzureOpenAIEndpoint = endpoint, EmbeddingDimensions = dimensions });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(message, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Project_override_inherits_unset_values_and_can_be_removed()
    {
        await using var app = await AdminApp.CreateAsync(postgres);
        await app.Admin.PutAsJsonAsync(Url("/api/admin/settings"), OrgSettings("org-key-12345"));

        var overridden = await app.Admin.PutAsJsonAsync(Url($"/api/admin/projects/{app.Ado.Repository.ProjectId}/settings"),
            new SaveSettingsRequest(null, "gpt-6.1-sol", null, null, null, null, null, null, null, null));
        var settings = (await overridden.Content.ReadFromJsonAsync<SaveSettingsResponse>())!.Settings;
        Assert.Equal("gpt-6.1-sol", settings.Project!.ChatDeployment);
        Assert.Null(settings.Project.AzureOpenAIEndpoint); // inherited
        Assert.Null(settings.Problem);

        Assert.Equal(HttpStatusCode.NoContent, (await app.Admin.DeleteAsync(Url($"/api/admin/projects/{app.Ado.Repository.ProjectId}/settings"))).StatusCode);
        var after = await app.Admin.GetFromJsonAsync<SettingsResponse>(Url($"/api/admin/settings?projectId={app.Ado.Repository.ProjectId}"));
        Assert.Null(after!.Project!.ChatDeployment);
    }

    [Fact]
    public async Task Test_settings_runs_one_embedding_and_one_chat_call()
    {
        await using var app = await AdminApp.CreateAsync(postgres);

        var notConfigured = await (await app.Admin.PostAsJsonAsync(Url("/api/admin/settings/test"), new TestSettingsRequest(null))).Content.ReadFromJsonAsync<TestSettingsResponse>();
        Assert.False(notConfigured!.Embedding.Ok);
        Assert.StartsWith("Not run", notConfigured.Embedding.Message, StringComparison.Ordinal);

        await app.Admin.PutAsJsonAsync(Url("/api/admin/settings"), OrgSettings("org-key-12345"));
        var result = await (await app.Admin.PostAsJsonAsync(Url("/api/admin/settings/test"), new TestSettingsRequest(null))).Content.ReadFromJsonAsync<TestSettingsResponse>();
        Assert.True(result!.Embedding.Ok, result.Embedding.Message);
        Assert.Contains("1536-dimension", result.Embedding.Message, StringComparison.Ordinal);
        Assert.True(result.Chat.Ok, result.Chat.Message);
    }

    [Fact]
    public async Task Admin_chooses_branches_to_index_and_removing_them_clears_the_index()
    {
        await using var app = await AdminApp.CreateAsync(postgres);
        var repoId = app.Ado.Repository.Id;

        var repos = await app.Admin.GetFromJsonAsync<List<AdminRepositoryDto>>(Url($"/api/admin/projects/{app.Ado.Repository.ProjectId}/repositories"));
        Assert.Equal(("web-app", false), (Assert.Single(repos!).Name, repos![0].Indexed));
        Assert.Equal(["refs/heads/main", "refs/heads/release"], await app.Admin.GetFromJsonAsync<List<string>>(Url($"/api/admin/repositories/{repoId}/branches")));

        var set = await app.Admin.PutAsJsonAsync(Url($"/api/admin/repositories/{repoId}/branches"), new SetBranchesRequest(["main", "release"]));
        var repo = (await set.Content.ReadFromJsonAsync<AdminRepositoryDto>())!;
        Assert.True(repo.Indexed);
        Assert.Equal(["refs/heads/main", "refs/heads/release"], repo.Branches.Select(b => b.Name));
        Assert.True(repo.Branches[0].IsDefault);
        Assert.All(repo.Branches, b => Assert.Equal("Queued", b.Status));
        await using (var db = app.Db.NewContext())
        {
            Assert.Equal(2, await db.IndexJobs.CountAsync(j => j.Type == IndexJobType.Full));
        }

        // Pretend release was indexed, then remove it: its chunks go too.
        var release = new RepoBranch(repoId, "refs/heads/release");
        await using (var db = app.Db.NewContext())
        {
            await new PgVectorStore(db).UpsertAsync([new CodeChunk("x", release, "a.cs", "csharp", 1, 1, null, "class A {}", "c", new float[1536])], CancellationToken.None);
        }

        await app.Admin.PutAsJsonAsync(Url("/api/admin/settings"), OrgSettings("org-key-12345")); // needed to find the store
        var trimmed = await (await app.Admin.PutAsJsonAsync(Url($"/api/admin/repositories/{repoId}/branches"), new SetBranchesRequest(["main"]))).Content.ReadFromJsonAsync<AdminRepositoryDto>();
        Assert.Equal(["refs/heads/main"], trimmed!.Branches.Select(b => b.Name));
        await using (var db = app.Db.NewContext())
        {
            Assert.False(await db.CodeChunks.AnyAsync(c => c.Branch == "refs/heads/release"));
        }

        var unknown = await app.Admin.PutAsJsonAsync(Url($"/api/admin/repositories/{repoId}/branches"), new SetBranchesRequest(["nope"]));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Contains("don't exist in web-app: nope", await unknown.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.Accepted, (await app.Admin.PostAsJsonAsync(Url($"/api/admin/repositories/{repoId}/reindex"), new ReindexRequest("main", Full: true))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Admin.PostAsJsonAsync(Url($"/api/admin/repositories/{repoId}/reindex"), new ReindexRequest("release", Full: false))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.Member.PutAsJsonAsync(Url($"/api/admin/repositories/{repoId}/branches"), new SetBranchesRequest(["main"]))).StatusCode);
    }

    [Fact]
    public async Task Changing_the_embedding_deployment_queues_a_full_reindex()
    {
        await using var app = await AdminApp.CreateAsync(postgres);
        await app.Admin.PutAsJsonAsync(Url("/api/admin/settings"), OrgSettings("org-key-12345"));
        await app.Admin.PutAsJsonAsync(Url($"/api/admin/repositories/{app.Ado.Repository.Id}/branches"), new SetBranchesRequest(["main"]));
        await using (var db = app.Db.NewContext())
        {
            // As if indexing finished with the current model.
            await db.IndexJobs.ExecuteDeleteAsync();
            await db.IndexedBranches.ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, BranchIndexStatus.Ready)
                .SetProperty(b => b.EmbeddingModel, "azure-openai:contoso.openai.azure.com/text-embedding-3-small")
                .SetProperty(b => b.EmbeddingDimensions, 1536));
        }

        var unchanged = await (await app.Admin.PutAsJsonAsync(Url("/api/admin/settings"), OrgSettings(null) with { ChatDeployment = "gpt-6.1-terra" })).Content.ReadFromJsonAsync<SaveSettingsResponse>();
        Assert.Equal(0, unchanged!.ReindexQueued);

        var changed = await (await app.Admin.PutAsJsonAsync(Url("/api/admin/settings"), OrgSettings(null) with { EmbeddingDeployment = "text-embedding-3-large" })).Content.ReadFromJsonAsync<SaveSettingsResponse>();
        Assert.Equal(1, changed!.ReindexQueued);
    }

    [Fact]
    public async Task Moving_to_another_vector_store_reindexes_and_clears_the_old_store()
    {
        await using var app = await AdminApp.CreateAsync(postgres);
        await app.Admin.PutAsJsonAsync(Url("/api/admin/settings"), OrgSettings("org-key-12345"));
        await app.Admin.PutAsJsonAsync(Url($"/api/admin/repositories/{app.Ado.Repository.Id}/branches"), new SetBranchesRequest(["main"]));
        var main = new RepoBranch(app.Ado.Repository.Id, "refs/heads/main");
        await using (var db = app.Db.NewContext())
        {
            await db.IndexJobs.ExecuteDeleteAsync();
            await db.IndexedBranches.ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, BranchIndexStatus.Ready)
                .SetProperty(b => b.EmbeddingModel, "azure-openai:contoso.openai.azure.com/text-embedding-3-small")
                .SetProperty(b => b.EmbeddingDimensions, 1536));
            await new PgVectorStore(db).UpsertAsync([new CodeChunk("x", main, "a.cs", "csharp", 1, 1, null, "class A {}", "c", new float[1536])], CancellationToken.None);
        }

        var moved = await app.Admin.PutAsJsonAsync(Url("/api/admin/settings"), OrgSettings(null) with
        {
            VectorStore = "AzureAISearch",
            AzureSearchEndpoint = "https://contoso-search.search.windows.net",
            AzureSearchIndexName = "chat-with-code",
            AzureSearchApiKey = "search-key-123456",
        });

        Assert.Equal(1, (await moved.Content.ReadFromJsonAsync<SaveSettingsResponse>())!.ReindexQueued);
        await using var check = app.Db.NewContext();
        Assert.False(await check.CodeChunks.AnyAsync()); // the pgvector copy is gone
    }

    [Fact]
    public async Task Invalid_search_index_name_is_explained()
    {
        await using var app = await AdminApp.CreateAsync(postgres);

        var response = await app.Admin.PutAsJsonAsync(Url("/api/admin/settings"), OrgSettings("k") with { VectorStore = "AzureAISearch", AzureSearchIndexName = "Code_Chunks" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("lowercase letters, digits or dashes", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private static SaveSettingsRequest OrgSettings(string? apiKey) => new(
        "https://contoso.openai.azure.com/", "gpt-6-luna", "text-embedding-3-small", 1536, false, apiKey, "PgVector", null, null, null);

    private static Uri Url(string path) => new(path, UriKind.Relative);

    private sealed class AdminApp : IAsyncDisposable
    {
        private readonly WebApplicationFactory<ServiceInfo> _factory;

        private AdminApp(WebApplicationFactory<ServiceInfo> factory, TestDb db, FakeAdoGitClient ado)
        {
            _factory = factory;
            Db = db;
            Ado = ado;
            Admin = Client("ada-token");
            Member = Client("grace-token");
        }

        public TestDb Db { get; }
        public FakeAdoGitClient Ado { get; }
        public HttpClient Admin { get; }
        public HttpClient Member { get; }

        public static async Task<AdminApp> CreateAsync(PostgresFixture postgres)
        {
            var connectionString = await postgres.CreateDatabaseAsync();
            var ado = new FakeAdoGitClient();
            ado.Commit(new() { ["README.md"] = "# hi" });
            ado.Commit(new() { ["README.md"] = "# release" }, branch: "refs/heads/release");
            var factory = new WebApplicationFactory<ServiceInfo>().WithWebHostBuilder(b => b
                .UseSetting("ConnectionStrings:Metadata", connectionString)
                .UseSetting("AzureDevOps:Organization", "contoso")
                .UseSetting("AzureDevOps:ExtensionSecret", Secret)
                .UseSetting("Admin:Users", "ADA@contoso.com, someone-else@contoso.com")
                .UseSetting("Security:DataProtectionKeysPath", Directory.CreateTempSubdirectory("cwc-keys").FullName)
                .ConfigureTestServices(services =>
                {
                    services.AddSingleton<IAdoUserClient>(new UserClient(ado));
                    services.AddSingleton<IAdoGitClient>(ado);
                    services.AddSingleton<IEmbedderFactory>(new FakeEmbedderFactory(new FakeEmbedder("fake")));
                    services.AddSingleton<IChatModelFactory>(new OkChatModelFactory());
                }));
            factory.CreateClient().Dispose();
            return new AdminApp(factory, TestDb.Create(connectionString), ado);
        }

        private HttpClient Client(string userToken)
        {
            var client = _factory.CreateClient();
            var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Expires = DateTime.UtcNow.AddMinutes(30),
                SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)), SecurityAlgorithms.HmacSha256),
            });
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);
            client.DefaultRequestHeaders.Add(RequireUserAccessFilter.HeaderName, userToken);
            return client;
        }

        public async ValueTask DisposeAsync()
        {
            Admin.Dispose();
            Member.Dispose();
            await Db.DisposeAsync();
            await _factory.DisposeAsync();
        }
    }

    private sealed class UserClient(FakeAdoGitClient ado) : IAdoUserClient
    {
        public Task<AdoUser> GetUserAsync(string userAccessToken, CancellationToken cancellationToken) => Task.FromResult(userAccessToken == "ada-token"
            ? new AdoUser("user-ada", "Ada Lovelace", "ada@contoso.com")
            : new AdoUser("user-grace", "Grace Hopper", "grace@contoso.com"));

        public Task<IReadOnlyList<ReadableRepository>> GetReadableRepositoriesAsync(string userAccessToken, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ReadableRepository>>(
                [new ReadableRepository(ado.Repository.Id, ado.Repository.Name, ado.Repository.ProjectId, ado.Repository.ProjectName, "refs/heads/main")]);
    }

    private sealed class OkChatModelFactory : IChatModelFactory
    {
        public IChatModel Create(EffectiveModelSettings settings) => new OkModel();

        private sealed class OkModel : IChatModel
        {
            public async IAsyncEnumerable<ChatDelta> StreamAsync(ChatRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
            {
                await Task.Yield();
                yield return new ChatDelta("OK");
            }
        }
    }
}
