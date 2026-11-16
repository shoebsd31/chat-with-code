using System.Reflection;
using ChatWithCode.Api;
using ChatWithCode.Core.Options;
using ChatWithCode.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

if (args is ["healthcheck", ..])
{
    return await HealthProbe.RunAsync();
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<AzureDevOpsOptions>()
    .Bind(builder.Configuration.GetSection(AzureDevOpsOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<IndexingOptions>()
    .Bind(builder.Configuration.GetSection(IndexingOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<SecurityOptions>()
    .Bind(builder.Configuration.GetSection(SecurityOptions.SectionName));

builder.Services.AddChatWithCodeData(builder.Configuration);

var security = builder.Configuration.GetSection(SecurityOptions.SectionName).Get<SecurityOptions>() ?? new SecurityOptions();
builder.Services.AddDataProtection()
    .SetApplicationName("ChatWithCode")
    .PersistKeysToFileSystem(new DirectoryInfo(security.DataProtectionKeysPath));

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
{
    if (security.AllowedOrigins.Count > 0)
    {
        p.WithOrigins([.. security.AllowedOrigins]).AllowAnyHeader().AllowAnyMethod();
    }
}));

builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks()
    .AddDbContextCheck<ChatWithCodeDbContext>("database", tags: ["ready"]);

var app = builder.Build();

if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    await using var scope = app.Services.CreateAsyncScope();
    var organization = scope.ServiceProvider.GetRequiredService<IOptions<AzureDevOpsOptions>>().Value.Organization;
    await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync(organization);
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors();

// Liveness: the process is up. Readiness: the database is reachable too.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResultStatusCodes = { [HealthStatus.Degraded] = StatusCodes.Status200OK },
});

app.MapGet("/api/info", (IOptions<AzureDevOpsOptions> ado) => new ServiceInfo(
        "Chat with Code",
        typeof(ServiceInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev",
        ado.Value.Organization))
    .WithName("GetInfo");

await app.RunAsync();
return 0;
