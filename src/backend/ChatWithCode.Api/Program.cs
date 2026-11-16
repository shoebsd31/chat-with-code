using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using ChatWithCode.Api;
using ChatWithCode.Api.Auth;
using ChatWithCode.Api.Chat;
using ChatWithCode.AzureDevOps;
using ChatWithCode.AzureOpenAI;
using ChatWithCode.Core.Abstractions;
using ChatWithCode.Core.Indexing;
using ChatWithCode.Core.Options;
using ChatWithCode.Data;
using ChatWithCode.Data.Settings;
using ChatWithCode.VectorStores.PgVector;
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
builder.Services.AddOptions<ChatOptions>()
    .Bind(builder.Configuration.GetSection(ChatOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddChatWithCodeData(builder.Configuration);
builder.Services.AddAzureDevOps();
builder.Services.AddPgVectorStore();
builder.Services.AddScoped<IVectorStoreFactory, KeyedVectorStoreFactory>();
builder.Services.AddSingleton<IEmbedderFactory>(new AzureOpenAIEmbedderFactory());
builder.Services.AddSingleton<IChatModelFactory>(new AzureOpenAIChatModelFactory());
builder.Services.AddSingleton<ITokenCounter, Cl100kTokenCounter>();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<UserAccessService>();
builder.Services.AddScoped<ChatService>();
builder.Services.AddExtensionAuthentication(builder.Configuration);

// Per-user limit on chat requests (each one costs model tokens).
var chatLimits = builder.Configuration.GetSection(ChatOptions.SectionName).Get<ChatOptions>() ?? new ChatOptions();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = async (context, ct) =>
        await Results.Problem($"You've sent {chatLimits.RequestsPerMinute} questions in the last minute. Wait a moment and try again.",
            statusCode: StatusCodes.Status429TooManyRequests, title: "Too many questions").ExecuteAsync(context.HttpContext);
    o.AddPolicy(ChatEndpoints.RateLimitPolicy, http =>
    {
        var token = http.Request.Headers[RequireUserAccessFilter.HeaderName].ToString();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = chatLimits.RequestsPerMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });
    });
});

var security = builder.Configuration.GetSection(SecurityOptions.SectionName).Get<SecurityOptions>() ?? new SecurityOptions();

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
    await scope.ServiceProvider.GetRequiredService<ModelSettingsBootstrapper>().ApplyAsync();
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// Liveness: the process is up. Readiness: the database is reachable too.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResultStatusCodes = { [HealthStatus.Degraded] = StatusCodes.Status200OK },
});

// Anonymous: the extension's settings page uses it to check that it reached the right backend.
app.MapGet("/api/info", (IOptions<AzureDevOpsOptions> ado) => new ServiceInfo(
        "Chat with Code",
        typeof(ServiceInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev",
        ado.Value.Organization,
        ExtensionConnected: !string.IsNullOrWhiteSpace(ado.Value.ExtensionSecret)))
    .WithName("GetInfo");

app.MapChatEndpoints();

await app.RunAsync();
return 0;
