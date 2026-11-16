using System.Net;
using Azure.Core;
using ChatWithCode.AzureDevOps;
using ChatWithCode.Core.Abstractions;
using ChatWithCode.Tests.Infrastructure;

namespace ChatWithCode.Tests.Adapters;

public sealed class AdoGitClientTests
{
    private static readonly Guid RepoId = Guid.Parse("6f1d7e2a-0000-4000-8000-000000000001");

    [Fact]
    public async Task Gets_repository_by_project_and_name_with_bearer_token()
    {
        var stub = new StubHttpHandler().On("/contoso/Fabrikam%20Web/_apis/git/repositories/web-app?api-version=7.1", HttpStatusCode.OK, $$$"""
            {"id":"{{{RepoId}}}","name":"web-app","defaultBranch":"refs/heads/main","project":{"id":"{{{Guid.Empty}}}","name":"Fabrikam Web"}}
            """);
        var client = Create(stub);

        var repo = await client.GetRepositoryAsync("Fabrikam Web", "web-app", CancellationToken.None);

        Assert.Equal(new AdoRepository(RepoId, "web-app", Guid.Empty, "Fabrikam Web", "refs/heads/main"), repo);
        Assert.Equal("Bearer test-token", stub.Requests[0].Request.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task Missing_repository_returns_null() =>
        Assert.Null(await Create(new StubHttpHandler()).GetRepositoryAsync("Fabrikam", "nope", CancellationToken.None));

    [Fact]
    public async Task Branch_head_matches_the_exact_ref_name()
    {
        var stub = new StubHttpHandler().On($"/contoso/_apis/git/repositories/{RepoId}/refs?filter=heads%2Fmain", HttpStatusCode.OK, """
            {"value":[{"name":"refs/heads/main-old","objectId":"aaa"},{"name":"refs/heads/main","objectId":"bbb"}],"count":2}
            """);

        Assert.Equal("bbb", await Create(stub).GetBranchHeadAsync(RepoId, "refs/heads/main", CancellationToken.None));
    }

    [Fact]
    public async Task Lists_blobs_only_with_sizes()
    {
        var stub = new StubHttpHandler()
            .On($"/contoso/_apis/git/repositories/{RepoId}/commits/c1?", HttpStatusCode.OK, """{"commitId":"c1","treeId":"t1"}""")
            .On($"/contoso/_apis/git/repositories/{RepoId}/trees/t1?recursive=true", HttpStatusCode.OK, """
                {"objectId":"t1","treeEntries":[
                  {"relativePath":"src","gitObjectType":"tree","objectId":"t2","size":0,"mode":"040000"},
                  {"relativePath":"src/App.cs","gitObjectType":"blob","objectId":"b1","size":120,"mode":"100644"},
                  {"relativePath":"link","gitObjectType":"blob","objectId":"b2","size":7,"mode":"120000"},
                  {"relativePath":"libs/shared","gitObjectType":"commit","objectId":"s1","size":0,"mode":"160000"}
                ]}
                """);

        var files = await Create(stub).GetFilesAsync(RepoId, "c1", CancellationToken.None);

        Assert.Equal([new AdoTreeEntry("src/App.cs", "b1", 120)], files);
    }

    [Fact]
    public async Task Downloads_blob_content()
    {
        var stub = new StubHttpHandler().On(
            r => r.RequestUri!.PathAndQuery.Contains("/blobs/b1?%24format=octetstream", StringComparison.Ordinal),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });

        Assert.Equal([1, 2, 3], await Create(stub).GetBlobAsync(RepoId, "b1", CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NonAuthoritativeInformation)]
    public async Task Access_problems_explain_what_to_grant(HttpStatusCode status)
    {
        var stub = new StubHttpHandler().On("/contoso/Fabrikam/", status, "<html>sign in</html>", "text/html");

        var error = await Assert.ThrowsAsync<AdoAccessException>(() => Create(stub).GetRepositoryAsync("Fabrikam", "web-app", CancellationToken.None));
        Assert.Contains("service principal", error.Message, StringComparison.Ordinal);
    }

    private static AdoGitClient Create(StubHttpHandler stub)
    {
        var auth = new ServicePrincipalTokenHandler(new StaticTokenCredential()) { InnerHandler = stub };
        return new AdoGitClient(new HttpClient(auth) { BaseAddress = new Uri("https://dev.azure.com/contoso/") });
    }

    private sealed class StaticTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Assert.Equal([ServicePrincipalTokenHandler.AzureDevOpsScope], requestContext.Scopes);
            return new AccessToken("test-token", DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}
