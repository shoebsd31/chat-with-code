# Chat with Code for Azure DevOps

Chat with the code and markdown in your Azure DevOps repositories, from inside Azure DevOps,
using your own Azure OpenAI deployment and your own vector database. Answers come only from
your indexed repositories and link to the exact files and lines they used.

Everything runs on infrastructure you host. No GitHub Copilot or Claude is involved.

> **Status: phase 4 of 8 done.** The backend indexes repositories and answers questions about
> them. The Azure DevOps extension adds **Repos > Chat with code** (streamed answers with links to
> the exact lines, diagrams, saved conversations) and settings pages where admins choose the
> Azure OpenAI deployments and which repositories and branches are indexed. GitHub Actions build
> and publish the extension. Next: Azure AI Search, automatic re-indexing on push, hardening,
> and an Azure Container Apps deployment. See [docs/DESIGN.md](docs/DESIGN.md) for the full design and roadmap.

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
| `AZURE_OPENAI_CHAT_DEPLOYMENT` | Your chat deployment's name (default `gpt-6-luna`) |
| `ADO_EXTENSION_SECRET` | The extension's secret (see [Package the extension](#package-the-extension)) |
| `CHAT_ADMINS` | Sign-in emails of the people who may change settings, comma-separated |

The Azure OpenAI values are saved as the organization defaults the first time the backend
starts. After that they're managed in **Organization settings > Chat with code**, and changing
`.env` has no effect on them. (You can also leave them out of `.env` and enter everything there.)

Start everything:

```bash
docker compose up -d --build
```

Check that it's running:

```bash
curl http://localhost:8080/health/ready
```

It prints `Healthy`.

### Choose what to index

In Azure DevOps, open **Project settings > Chat with code**. Under **Repositories**, select
**Index** (or **Change branches**) on a repository, pick branches, and **Save**. Indexing runs in
the background and the page shows progress. **Re-index** picks up changes since the last run
(or starts over after a failure).

Only people listed in `CHAT_ADMINS` can change settings; everyone else sees them read-only.
Admins are kept in the backend's configuration on purpose: they can't be changed from Azure
DevOps, and checking Azure DevOps admin roles would need a high-privilege extension scope.

The same can be done from the command line, which is handy for scripts:

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

## Package the extension

The extension lives in `src/extension` (React, built with Vite). Each organization publishes
its own private copy, with its backend address built in:

```bash
cd src/extension
```

```bash
npm ci
```

```bash
npm run package -- --backend-url https://chat.example.com --publisher your-publisher-id --version 1.0.0
```

This writes `out/your-publisher-id.chat-with-code-1.0.0.vsix`. Upload it in the
[Marketplace publisher portal](https://marketplace.visualstudio.com/manage), share it with your
organization and install it. Then:

1. Copy the extension's secret (publisher portal > extension > **Certificate**) into
   `ADO_EXTENSION_SECRET` on the backend.
2. Set `EXTENSION_ORIGIN=https://your-publisher-id.gallerycdn.vsassets.io` on the backend, so
   the browser may call it (CORS).
3. Open **Organization settings > Chat with code** and select **Test connection**.

The backend address is built in on purpose. Settings an extension stores for the whole
organization can be changed by any of its users, so a stored address could be redirected to
collect everyone's sign-in tokens.

To work on the screens without Azure DevOps, run `npm run dev` and open
`http://localhost:5173/hub.html` (add `?theme=dark` for the dark theme). It uses sample data
and never contacts a backend. `npm test` runs the unit tests.

## Publish with GitHub Actions

Two workflows live in `.github/workflows`:

| Workflow | When | What |
|---|---|---|
| **Extension CI** | Every pull request and push to `main` that touches the extension | Unit tests and packaging; the `.vsix` is attached to the run |
| **Publish extension** | Pushing a tag like `extension-v1.2.0`, or **Run workflow** in the Actions tab | Tests, packages with your backend address, and publishes to the Marketplace as a private extension shared with your organization |

Publishing signs in as a Microsoft Entra app using GitHub's OIDC token (workload identity
federation), as Microsoft recommends. No Marketplace token or client secret is stored in GitHub.

**One-time setup**

1. **Create the Entra app.** In the Azure portal: **Microsoft Entra ID > App registrations > New
   registration**, for example `chat-with-code-publisher`. Copy its **Application (client) ID**
   and **Directory (tenant) ID**.
2. **Trust this repository.** On the app: **Certificates & secrets > Federated credentials > Add
   credential > GitHub Actions deploying Azure resources**. Enter your GitHub organization and
   repository, entity type **Environment**, environment name `marketplace`.
3. **Create the GitHub environment.** In the repository: **Settings > Environments > New
   environment** named `marketplace`. Optionally add required reviewers, so each release needs
   approval. Add these environment variables:

   | Variable | Value |
   |---|---|
   | `AZURE_CLIENT_ID` | The app's application (client) ID |
   | `AZURE_TENANT_ID` | Your tenant ID |
   | `MARKETPLACE_PUBLISHER` | Your Marketplace publisher ID |
   | `ADO_ORGANIZATION` | The Azure DevOps organization to share the extension with |
   | `CWC_BACKEND_URL` | Your backend address, for example `https://chat.contoso.com` |

4. **Run the workflow once.** It packages, signs in, and prints the line
   `Publishing as Marketplace identity: <id> <name>`. The first run fails at the publish step,
   because the app isn't a member of your publisher yet.
5. **Add the app to your publisher.** In the [Marketplace publisher portal](https://marketplace.visualstudio.com/manage):
   your publisher > **Members** > **Add**. Enter the ID printed in step 4 and choose **Contributor**.
6. **Run the workflow again.** It publishes and shares the extension.

To release a new version, push a tag (the version must be higher than the published one):

```bash
git tag extension-v1.0.1
```

```bash
git push origin extension-v1.0.1
```

Organizations that already have the extension installed get the update automatically. If the
extension's scopes change, Azure DevOps asks an admin to approve the update, and the extension
secret changes too: copy the new one into `ADO_EXTENSION_SECRET`.

## Chat API

The extension (phase 4) talks to these endpoints. Every `/api/*` call except `/api/info`
needs two headers:

| Header | Value | Checked how |
|---|---|---|
| `Authorization: Bearer …` | The extension's app token (`SDK.getAppToken()`) | Signature checked against `ADO_EXTENSION_SECRET` (Marketplace publisher portal > extension > Certificate). Proves the call came from the extension. |
| `X-AzureDevOps-Token` | The user's access token (`SDK.getAccessToken()`) | Never decoded. Passed to Azure DevOps, which confirms the user and lists the repositories they can read (cached 5 minutes). |

| Endpoint | What it does |
|---|---|
| `GET /api/info` | Name, version, organization, and whether the extension secret is set (no sign-in needed) |
| `GET /api/me` | The signed-in user |
| `GET /api/repositories` | Indexed repositories the user can read, with branches and index status |
| `POST /api/chat` | Ask a question; the answer streams back as Server-Sent Events |
| `GET /api/conversations` | The user's conversations, newest first |
| `GET /api/conversations/{id}` | One conversation with its messages and citations |
| `DELETE /api/conversations/{id}` | Delete a conversation |
| `GET /api/admin/settings[?projectId=]` | Organization defaults (and a project's overrides). Keys are never returned, only whether one is set |
| `PUT /api/admin/settings`, `PUT/DELETE /api/admin/projects/{id}/settings` | Save organization defaults or project overrides (admins). Changing the embedding model queues full re-indexing |
| `POST /api/admin/settings/test` | Try the saved settings with one embedding and one chat call (admins) |
| `GET /api/admin/projects/{id}/repositories` | A project's repositories with indexed branches and status |
| `GET/PUT /api/admin/repositories/{id}/branches` | List a repository's branches; set which are indexed (admins) |
| `POST /api/admin/repositories/{id}/reindex` | Queue re-indexing of a branch (admins) |

`POST /api/chat` body: `{ "question": "...", "targets": [{ "repositoryId": "<ADO repo id>", "branch": "refs/heads/main" }] }`.
To continue a conversation, send `conversationId` instead of (or as well as) `targets`.
The stream sends these events in order:

| Event | Data |
|---|---|
| `conversation` | `conversationId`, `title`, `question` |
| `delta` | `text`: the next piece of the answer (Markdown, may include ```` ```mermaid ```` diagrams) |
| `citations` | `citations` (number, repository, branch, path, lines, symbol, `url` to the lines in Azure DevOps, `used`) and `answerFound` |
| `done` | `messageId` and token usage |
| `error` | `message`, safe to show to the user |

When the selected repositories don't contain the answer, the reply says so and `citations`
lists the closest files with `used: false` (and `answerFound: false`). Problems found before
streaming starts (no access to a repository, branch not indexed, empty question) return an
HTTP error with a readable `detail`. Each user can ask `CHAT_REQUESTS_PER_MINUTE` questions per
minute (default 20), and conversations with no new messages for `CHAT_RETENTION_DAYS` (default 30)
are deleted.

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
| `AzureDevOps:ExtensionSecret` | Yes, to chat | — | Validates calls from the extension |
| `Chat:RetentionDays` | No | 30 | Delete conversations with no new messages for this long |
| `Chat:RequestsPerMinute` | No | 20 | Questions per user per minute |
| `Chat:MaxSources` / `ContextTokens` / `MaxAnswerTokens` | No | 12 / 8000 / 4000 | How much code goes into the prompt, and the answer length limit |
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
  ChatWithCode.Api/                   HTTP API: sign-in checks, chat (streaming), conversations
  ChatWithCode.Worker/                indexer (job loop, RepositoryIndexer, index/status commands)
  ChatWithCode.Core/                  options, interfaces, file filtering, chunking, prompts
  ChatWithCode.Data/                  EF Core metadata database, migrations, job queue, settings
  ChatWithCode.AzureDevOps/           Git REST client (service principal) and user checks (user token)
  ChatWithCode.AzureOpenAI/           embeddings and streaming chat
  ChatWithCode.VectorStores.PgVector/ pgvector store with hybrid search
  tests/ChatWithCode.Tests/           unit and integration tests
```
