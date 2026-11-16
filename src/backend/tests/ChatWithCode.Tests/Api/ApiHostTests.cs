using System.Net;
using System.Net.Http.Json;
using ChatWithCode.Api;
using ChatWithCode.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ChatWithCode.Tests.Api;

[Collection(PostgresCollection.Name)]
public sealed class ApiHostTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Health_and_info_endpoints_respond()
    {
        await using var factory = await CreateFactoryAsync(organization: "contoso");
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri("/health/live", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri("/health/ready", UriKind.Relative))).StatusCode);

        var info = await client.GetFromJsonAsync<ServiceInfo>(new Uri("/api/info", UriKind.Relative));
        Assert.NotNull(info);
        Assert.Equal("contoso", info.Organization);
    }

    [Fact]
    public async Task Startup_fails_when_organization_is_a_url()
    {
        await using var factory = await CreateFactoryAsync(organization: "https://dev.azure.com/contoso");

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("organization name", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private async Task<WebApplicationFactory<ServiceInfo>> CreateFactoryAsync(string organization)
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var keys = Directory.CreateTempSubdirectory("cwc-keys").FullName;

        return new WebApplicationFactory<ServiceInfo>().WithWebHostBuilder(b => b
            .UseSetting("ConnectionStrings:Metadata", connectionString)
            .UseSetting("AzureDevOps:Organization", organization)
            .UseSetting("Security:DataProtectionKeysPath", keys));
    }
}
