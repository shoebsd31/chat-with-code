using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using ChatWithCode.Api;
using ChatWithCode.Api.Webhooks;
using ChatWithCode.Core.Domain;
using ChatWithCode.Data.Entities;
using ChatWithCode.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace ChatWithCode.Tests.Api;

[Collection(PostgresCollection.Name)]
public sealed class PushWebhookTests(PostgresFixture postgres)
{
    private static readonly Guid RepoId = Guid.NewGuid();
    private const string NewCommit = "1111111111111111111111111111111111111111";

    [Fact]
    public async Task Push_to_an_indexed_branch_queues_it_at_the_pushed_commit()
    {
        await using var app = await WebhookApp.CreateAsync(postgres);

        var response = await app.Client.PostAsync(Url(), Push(RepoId, ("refs/heads/main", NewCommit), ("refs/heads/not-indexed", NewCommit)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, (await response.Content.ReadFromJsonAsync<PushWebhookEndpoint.PushResult>())!.Queued);
        await using var db = app.Db.NewContext();
        var job = await db.IndexJobs.SingleAsync();
        Assert.Equal((IndexJobType.Incremental, NewCommit), (job.Type, job.TargetCommit));
        Assert.NotNull((await db.Repositories.SingleAsync()).LastPushNotificationAt);
    }

    [Fact]
    public async Task Unknown_repositories_deleted_branches_and_already_indexed_commits_queue_nothing()
    {
        await using var app = await WebhookApp.CreateAsync(postgres);

        await app.Client.PostAsync(Url(), Push(Guid.NewGuid(), ("refs/heads/main", NewCommit)));                          // not indexed
        await app.Client.PostAsync(Url(), Push(RepoId, ("refs/heads/main", new string('0', 40))));                      // branch deleted
        await app.Client.PostAsync(Url(), Push(RepoId, ("refs/heads/main", "abcabcabcabcabcabcabcabcabcabcabcabcabca"))); // already indexed
        await app.Client.PostAsync(Url(), Push(RepoId, ("refs/heads/main", "not-a-commit")));                           // malformed

        await using var db = app.Db.NewContext();
        Assert.False(await db.IndexJobs.AnyAsync());
    }

    [Fact]
    public async Task Wrong_credentials_are_rejected()
    {
        await using var app = await WebhookApp.CreateAsync(postgres);
        using var request = new HttpRequestMessage(HttpMethod.Post, Url()) { Content = Push(RepoId, ("refs/heads/main", NewCommit)) };
        request.Headers.Authorization = Basic("azdo", "wrong");

        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Client.SendAsync(request)).StatusCode);

        using var anonymous = app.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync(Url(), Push(RepoId, ("refs/heads/main", NewCommit)))).StatusCode);
    }

    [Fact]
    public async Task Other_events_and_bad_payloads_get_clear_answers()
    {
        await using var app = await WebhookApp.CreateAsync(postgres);

        var other = await app.Client.PostAsync(Url(), Json("""{"eventType":"git.pullrequest.created","resource":{}}"""));
        Assert.Contains("Ignored", (await other.Content.ReadFromJsonAsync<PushWebhookEndpoint.PushResult>())!.Message, StringComparison.Ordinal);

        var minimal = await app.Client.PostAsync(Url(), Json("""{"eventType":"git.push","resource":{"url":"x"}}"""));
        Assert.Equal(HttpStatusCode.BadRequest, minimal.StatusCode);
        Assert.Contains("Resource details to send", await minimal.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.PostAsync(Url(), Json("not json"))).StatusCode);
    }

    [Fact]
    public async Task Without_configured_credentials_the_endpoint_is_off()
    {
        await using var app = await WebhookApp.CreateAsync(postgres, configureCredentials: false);

        var response = await app.Client.PostAsync(Url(), Push(RepoId, ("refs/heads/main", NewCommit)));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("WEBHOOK_USERNAME", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private static Uri Url() => new(PushWebhookEndpoint.Path, UriKind.Relative);

    private static AuthenticationHeaderValue Basic(string user, string password) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    /// <summary>The shape Azure DevOps sends for "Code pushed" with resource details set to All.</summary>
    private static StringContent Push(Guid repositoryId, params (string Ref, string NewCommit)[] updates) => Json(System.Text.Json.JsonSerializer.Serialize(new
    {
        eventType = "git.push",
        resource = new
        {
            refUpdates = updates.Select(u => new { name = u.Ref, oldObjectId = "abcabcabcabcabcabcabcabcabcabcabcabcabca", newObjectId = u.NewCommit }),
            repository = new { id = repositoryId, name = "web-app", project = new { id = Guid.NewGuid(), name = "Fabrikam" } },
        },
    }));

    private sealed class WebhookApp : IAsyncDisposable
    {
        private WebhookApp(WebApplicationFactory<ServiceInfo> factory, TestDb db)
        {
            Factory = factory;
            Db = db;
            Client = factory.CreateClient();
            Client.DefaultRequestHeaders.Authorization = Basic("azdo", "s3cret-webhook-password");
        }

        public WebApplicationFactory<ServiceInfo> Factory { get; }
        public TestDb Db { get; }
        public HttpClient Client { get; }

        public static async Task<WebhookApp> CreateAsync(PostgresFixture postgres, bool configureCredentials = true)
        {
            var connectionString = await postgres.CreateDatabaseAsync();
            var factory = new WebApplicationFactory<ServiceInfo>().WithWebHostBuilder(b =>
            {
                b.UseSetting("ConnectionStrings:Metadata", connectionString)
                    .UseSetting("AzureDevOps:Organization", "contoso")
                    .UseSetting("Security:DataProtectionKeysPath", Directory.CreateTempSubdirectory("cwc-keys").FullName);
                if (configureCredentials)
                {
                    b.UseSetting("Security:WebhookUsername", "azdo").UseSetting("Security:WebhookPassword", "s3cret-webhook-password");
                }
            });
            factory.CreateClient().Dispose();

            var db = TestDb.Create(connectionString);
            await using var seed = db.NewContext();
            var orgId = await seed.Organizations.Select(o => o.Id).SingleAsync();
            seed.Repositories.Add(new Repository
            {
                Id = Guid.NewGuid(), OrganizationId = orgId, AdoRepositoryId = RepoId, AdoProjectId = Guid.NewGuid(), ProjectName = "Fabrikam",
                Name = "web-app", CreatedAt = DateTimeOffset.UtcNow,
                Branches = [new IndexedBranch { Id = Guid.NewGuid(), BranchName = "refs/heads/main", Status = BranchIndexStatus.Ready, LastIndexedCommit = "abcabcabcabcabcabcabcabcabcabcabcabcabca" }],
            });
            await seed.SaveChangesAsync();
            return new WebhookApp(factory, db);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Db.DisposeAsync();
            await Factory.DisposeAsync();
        }
    }
}
