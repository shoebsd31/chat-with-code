using ChatWithCode.AzureDevOps;
using ChatWithCode.AzureOpenAI;
using ChatWithCode.Core.Abstractions;
using ChatWithCode.Core.Indexing;
using ChatWithCode.Core.Indexing.Chunking;
using ChatWithCode.Core.Options;
using ChatWithCode.Data;
using ChatWithCode.VectorStores.AzureAISearch;
using ChatWithCode.VectorStores.PgVector;
using ChatWithCode.Worker;
using ChatWithCode.Worker.Cli;
using ChatWithCode.Worker.Indexing;

var isCommand = AdminCommands.IsCommand(args);
var builder = Host.CreateApplicationBuilder(isCommand ? [] : args);
if (isCommand)
{
    // Keep command output readable: only warnings and errors from the framework.
    // (Added last, so it overrides the Information level in appsettings.json.)
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Logging:LogLevel:Default"] = "Warning",
        ["Logging:LogLevel:Microsoft"] = "Warning",
        ["Logging:LogLevel:System.Net.Http.HttpClient"] = "Warning",
    });
}

builder.Services.AddOptions<AzureDevOpsOptions>()
    .Bind(builder.Configuration.GetSection(AzureDevOpsOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<IndexingOptions>()
    .Bind(builder.Configuration.GetSection(IndexingOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<ChatOptions>()
    .Bind(builder.Configuration.GetSection(ChatOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddChatWithCodeData(builder.Configuration);
builder.Services.AddAzureDevOps();
builder.Services.AddPgVectorStore();
builder.Services.AddAzureAISearchStore();
builder.Services.AddSingleton<IEmbedderFactory>(new AzureOpenAIEmbedderFactory());
builder.Services.AddSingleton<IChatModelFactory>(new AzureOpenAIChatModelFactory());
builder.Services.AddScoped<IVectorStoreFactory, KeyedVectorStoreFactory>();
builder.Services.AddSingleton<ITokenCounter, Cl100kTokenCounter>();
builder.Services.AddSingleton(sp => new ChunkerSelector(sp.GetRequiredService<ITokenCounter>()));
builder.Services.AddScoped<QuestionSuggester>();
builder.Services.AddScoped<IIndexJobHandler, RepositoryIndexer>();

if (isCommand)
{
    builder.Services.AddScoped(sp => ActivatorUtilities.CreateInstance<AdminCommands>(sp, Console.Out));
    using var host = builder.Build();
    await using var scope = host.Services.CreateAsyncScope();
    return await scope.ServiceProvider.GetRequiredService<AdminCommands>().RunAsync(args, CancellationToken.None);
}

builder.Services.AddHostedService<IndexJobWorker>();
builder.Services.AddHostedService<ConversationRetentionService>();
builder.Services.AddHostedService<BranchPoller>();
builder.Services.AddHostedService<SuggestionBackfillService>();

// The API applies database migrations; the worker starts after it (see deploy/docker-compose.yml).
await builder.Build().RunAsync();
return 0;
