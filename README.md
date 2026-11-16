# Chat with Code for Azure DevOps

Chat with the code and markdown in your Azure DevOps repositories, from inside Azure DevOps,
using your own Azure OpenAI deployment and your own vector database. Answers come only from
your indexed repositories and link to the exact files and lines they used.

Everything runs on infrastructure you host. No GitHub Copilot or Claude is involved.

> **Status: phase 1 of 7.** The backend skeleton runs: API, worker, database and job queue.
> Indexing, chat and the Azure DevOps extension come in the next phases.
> See [docs/DESIGN.md](docs/DESIGN.md) for the full design and roadmap.

## Run the backend

You need Docker with the Compose plugin.

```bash
cd deploy
cp .env.example .env
```

Edit `.env`: set `ADO_ORGANIZATION` (the part after `https://dev.azure.com/`) and a long
random `POSTGRES_PASSWORD`. Then start everything:

```bash
docker compose up -d --build
```

Check that it's running:

```bash
curl http://localhost:8080/health/ready
```

```bash
curl http://localhost:8080/api/info
```

The first command prints `Healthy`. The second prints the service name, version and
organization.

What starts:

| Service | What it does |
|---|---|
| `db` | PostgreSQL 17 with pgvector. Holds settings, index state, the job queue and chat history. |
| `api` | ASP.NET Core API on port 8080 (`API_PORT`). Applies database migrations on start. |
| `worker` | Background indexer. Starts after the API is healthy and processes queued index jobs. |

Stored API keys are encrypted with a key ring kept in the `keys` volume. Back up both the
`pgdata` and `keys` volumes: without the key ring, saved API keys can't be decrypted.

## Configuration

All settings can be set in `deploy/.env`, or as environment variables using `__` for
nesting (for example `AzureDevOps__Organization`).

| Setting | Required | Default | Purpose |
|---|---|---|---|
| `AzureDevOps:Organization` | Yes | — | Organization name only, not the URL |
| `ConnectionStrings:Metadata` | Yes | set by compose | PostgreSQL connection |
| `AzureDevOps:ExtensionSecret` | From phase 3 | — | Validates calls from the extension |
| `AzureDevOps:ServicePrincipal:*` or `AzureDevOps:PersonalAccessToken` | From phase 2 | — | How the backend reads repositories (Code: Read) |
| `Indexing:MaxFileSizeKb` | No | 500 | Larger files are skipped |
| `Indexing:PollingEnabled` / `PollIntervalMinutes` | No | true / 5 | Check for new commits when push notifications can't reach the backend |
| `Security:AllowedOrigins` | From phase 4 | — | Browser origin of the installed extension |
| `Database:MigrateOnStartup` | No | true | Apply database migrations when the API starts |

## Develop

Requirements: .NET 10 SDK and Docker (the tests start a throwaway PostgreSQL container).

```bash
cd src/backend
```

```bash
dotnet test
```

To add a database migration after changing an entity:

```bash
dotnet tool restore
```

```bash
dotnet ef migrations add <Name> -p ChatWithCode.Data -o Migrations
```

## Repository layout

```
docs/DESIGN.md              design and roadmap
deploy/                     docker compose + .env.example
src/backend/
  ChatWithCode.Api/         HTTP API (health, info; chat and settings later)
  ChatWithCode.Worker/      background indexer
  ChatWithCode.Core/        options, domain types, interfaces for models and vector stores
  ChatWithCode.Data/        EF Core metadata database, migrations, job queue
  tests/ChatWithCode.Tests/ unit and integration tests
```
