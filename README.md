# Chat with Code for Azure DevOps

Chat with the code and markdown in your Azure DevOps repositories, from inside Azure DevOps,
using your own Azure OpenAI deployment and your own vector database. Answers come only from
your indexed repositories and link to the exact files and lines they used.

Everything runs on infrastructure you host. No GitHub Copilot or Claude is involved.

> **Status: phase 2 of 7.** The backend indexes repositories into pgvector: it reads them
> with a service principal, skips files that don't help, chunks code and markdown, and embeds
> them with Azure OpenAI. Chat, the Azure DevOps extension and automatic re-indexing on push
> come in the next phases. See [docs/DESIGN.md](docs/DESIGN.md) for the full design and roadmap.

## Before you start

You need:

- Docker with the Compose plugin, on a machine that can reach `dev.azure.com` and your Azure OpenAI resource.
- An **Azure OpenAI** resource with an embedding deployment (`text-embedding-3-small` recommended)
  and, for phase 3, a chat deployment.
- A **service principal** that can read your repositories (next section).

### Set up the service principal

The backend reads repositories as a Microsoft Entra application. No personal tokens.

1. In the Azure portal, open **Microsoft Entra ID > App registrations > New registration**.
   Give it a name like `chat-with-code-indexer` and register it. No redirect URI is needed.
2. On the app's **Overview** page, copy the **Directory (tenant) ID** and **Application (client) ID**.
3. Open **Certificates & secrets > New client secret** and copy the secret **value**.
4. In Azure DevOps, open **Organization settings > Users > Add users**, search for the app's
   name, give it **Basic** access, and add it to the projects you want to index.
   (Your Azure DevOps organization must be connected to the same Entra tenant.)
5. Make sure it can read the repositories: project members can by default. If a repository
   restricts access, open **Project settings > Repositories > (repository) > Security** and
   allow **Read** for the app.

## Run the backend

```bash
cd deploy
cp .env.example .env
```

Edit `.env`:

| Value | What to put |
|---|---|
| `ADO_ORGANIZATION` | The part after `https://dev.azure.com/` |
| `POSTGRES_PASSWORD` | A long random value |
| `ADO_SP_TENANT_ID`, `ADO_SP_CLIENT_ID`, `ADO_SP_CLIENT_SECRET` | From the service principal steps |
| `AZURE_OPENAI_ENDPOINT` | e.g. `https://your-resource.openai.azure.com/` |
| `AZURE_OPENAI_API_KEY` | Key from the Azure OpenAI resource (or set `AZURE_OPENAI_USE_MANAGED_IDENTITY=true`) |
| `AZURE_OPENAI_EMBEDDING_DEPLOYMENT` | Your embedding deployment's name |

The Azure OpenAI values are saved as the organization defaults the first time the backend
starts. After that they're managed in Azure DevOps (from phase 4), and changing `.env` has
no effect on them.

Start everything:

```bash
docker compose up -d --build
```

Check that it's running:

```bash
curl http://localhost:8080/health/ready
```

It prints `Healthy`.

### Index a repository

Until the settings pages exist in Azure DevOps (phase 4), add repositories from the command line:

```bash
docker compose run --rm worker index --project "Your Project" --repo your-repo
```

This queues the repository's default branch. Add `--branch release/2.0` for another branch,
or `--full` to re-read every file instead of only the ones that changed. Running the same
command again later indexes just what changed since the last run.

See progress:

```bash
docker compose run --rm worker status
```

```
Repository             Branch  Status          Files  Chunks  Last indexed          Commit
Fabrikam/web-app       main    Indexing 64/412 0      0       never                 -
```

### What gets indexed

Code and documentation in any language. These are skipped automatically:

- dependencies and build output (`node_modules/`, `bin/`, `obj/`, `dist/`, `vendor/`, …)
- lockfiles, minified files, source maps and generated files
- binary files (images, archives, executables, fonts, …) and files that aren't UTF-8 text
- files larger than `INDEX_MAX_FILE_KB` (500 KB by default)

To change this for one repository, add a `.chatwithcodeignore` file at its root. It uses
`.gitignore` syntax, and `!` re-includes something skipped by default:

```gitignore
# Large test fixtures don't help answer questions
tests/fixtures/
# Our build/ folder holds pipeline scripts, not build output
!build/
```

Files are split into chunks before embedding: small files stay whole, markdown is split by
heading, C# by class and method, and other languages at blank lines, all under about
1,500 tokens per chunk.

## What starts

| Service | What it does |
|---|---|
| `db` | PostgreSQL 17 with pgvector. Holds settings, the index (vectors and full-text), the job queue and chat history. |
| `api` | ASP.NET Core API on port 8080 (`API_PORT`). Applies database migrations on start. |
| `worker` | Background indexer. Starts after the API is healthy and processes queued index jobs. |

Stored API keys are encrypted with a key ring kept in the `keys` volume. Back up both the
`pgdata` and `keys` volumes: without the key ring, saved API keys can't be decrypted and
have to be entered again.

## Configuration

All settings can be set in `deploy/.env`, or as environment variables using `__` for
nesting (for example `AzureDevOps__Organization`).

| Setting | Required | Default | Purpose |
|---|---|---|---|
| `AzureDevOps:Organization` | Yes | — | Organization name only, not the URL |
| `AzureDevOps:ServicePrincipal:TenantId` / `ClientId` / `ClientSecret` | Yes, to index | — | How the backend reads repositories |
| `ConnectionStrings:Metadata` | Yes | set by compose | PostgreSQL connection |
| `AzureOpenAI:Endpoint`, `ApiKey`, `UseManagedIdentity`, `EmbeddingDeployment`, `ChatDeployment`, `EmbeddingDimensions` | First start | dimensions 1536 | Starting organization defaults (saved once) |
| `VectorStore:Kind` | No | `PgVector` | `AzureAISearch` arrives in phase 5 |
| `Indexing:MaxFileSizeKb` | No | 500 | Larger files are skipped |
| `Indexing:ExcludeGlobs` | No | — | Extra skip rules for every repository (`.gitignore` syntax) |
| `Indexing:PollingEnabled` / `PollIntervalMinutes` | No | true / 5 | Check for new commits when push notifications can't reach the backend (phase 6) |
| `AzureDevOps:ExtensionSecret` | From phase 3 | — | Validates calls from the extension |
| `Security:AllowedOrigins` | From phase 4 | — | Browser origin of the installed extension |
| `Database:MigrateOnStartup` | No | true | Apply database migrations when the API starts |

The pgvector store uses 1,536-dimension vectors. `text-embedding-3-small` produces these
by default, and `text-embedding-3-large` is asked for 1,536 dimensions automatically.

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
docs/DESIGN.md                        design and roadmap
deploy/                               docker compose + .env.example
src/backend/
  ChatWithCode.Api/                   HTTP API (health, info; chat and settings later)
  ChatWithCode.Worker/                indexer (job loop, RepositoryIndexer, index/status commands)
  ChatWithCode.Core/                  options, interfaces, file filtering, chunking
  ChatWithCode.Data/                  EF Core metadata database, migrations, job queue, settings
  ChatWithCode.AzureDevOps/           Git REST client (service principal)
  ChatWithCode.AzureOpenAI/           embeddings
  ChatWithCode.VectorStores.PgVector/ pgvector store
  tests/ChatWithCode.Tests/           unit and integration tests
```
