using ChatWithCode.Core.Abstractions;
using ChatWithCode.Core.Options;
using ChatWithCode.Data;
using ChatWithCode.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddOptions<AzureDevOpsOptions>()
    .Bind(builder.Configuration.GetSection(AzureDevOpsOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<IndexingOptions>()
    .Bind(builder.Configuration.GetSection(IndexingOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddChatWithCodeData(builder.Configuration);
builder.Services.AddScoped<IIndexJobHandler, NotAvailableIndexJobHandler>();
builder.Services.AddHostedService<IndexJobWorker>();

// The API applies database migrations; the worker starts after it (see deploy/docker-compose.yml).
await builder.Build().RunAsync();
