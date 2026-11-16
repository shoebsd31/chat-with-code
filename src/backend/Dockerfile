# Builds both services. Pick one with --target api or --target worker.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, so dependency layers are cached when only code changes.
COPY global.json Directory.Build.props .editorconfig ./
COPY ChatWithCode.Core/ChatWithCode.Core.csproj ChatWithCode.Core/
COPY ChatWithCode.Data/ChatWithCode.Data.csproj ChatWithCode.Data/
COPY ChatWithCode.AzureDevOps/ChatWithCode.AzureDevOps.csproj ChatWithCode.AzureDevOps/
COPY ChatWithCode.AzureOpenAI/ChatWithCode.AzureOpenAI.csproj ChatWithCode.AzureOpenAI/
COPY ChatWithCode.VectorStores.PgVector/ChatWithCode.VectorStores.PgVector.csproj ChatWithCode.VectorStores.PgVector/
COPY ChatWithCode.Api/ChatWithCode.Api.csproj ChatWithCode.Api/
COPY ChatWithCode.Worker/ChatWithCode.Worker.csproj ChatWithCode.Worker/
RUN dotnet restore ChatWithCode.Api/ChatWithCode.Api.csproj \
 && dotnet restore ChatWithCode.Worker/ChatWithCode.Worker.csproj

COPY ChatWithCode.Core/ ChatWithCode.Core/
COPY ChatWithCode.Data/ ChatWithCode.Data/
COPY ChatWithCode.AzureDevOps/ ChatWithCode.AzureDevOps/
COPY ChatWithCode.AzureOpenAI/ ChatWithCode.AzureOpenAI/
COPY ChatWithCode.VectorStores.PgVector/ ChatWithCode.VectorStores.PgVector/
COPY ChatWithCode.Api/ ChatWithCode.Api/
COPY ChatWithCode.Worker/ ChatWithCode.Worker/
RUN dotnet publish ChatWithCode.Api/ChatWithCode.Api.csproj -c Release -o /out/api --no-restore \
 && dotnet publish ChatWithCode.Worker/ChatWithCode.Worker.csproj -c Release -o /out/worker --no-restore

# Shared runtime base. The key folder holds the key ring that encrypts stored API keys.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN mkdir -p /var/lib/chatwithcode/keys && chown -R app:app /var/lib/chatwithcode
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1
WORKDIR /app

FROM runtime AS api
COPY --from=build /out/api .
USER app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=6s --start-period=20s --retries=6 \
  CMD ["dotnet", "ChatWithCode.Api.dll", "healthcheck"]
ENTRYPOINT ["dotnet", "ChatWithCode.Api.dll"]

FROM runtime AS worker
COPY --from=build /out/worker .
USER app
ENTRYPOINT ["dotnet", "ChatWithCode.Worker.dll"]
