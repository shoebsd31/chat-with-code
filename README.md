# Chat with Code for Azure DevOps

Chat with the code and markdown in your Azure DevOps repositories, from inside Azure DevOps,
using your own Azure OpenAI deployment and your own vector database. Answers come only from
your indexed repositories and link to the exact files and lines they used.

Everything runs on infrastructure you host. No GitHub Copilot or Claude is involved.

> **Status: phases 1–6 and 8 done.** The backend indexes repositories and answers questions about
> them. The Azure DevOps extension adds **Repos > Chat with code** (streamed answers with links to
> the exact lines, diagrams, saved conversations) and settings pages where admins choose the
> Azure OpenAI deployments and which repositories and branches are indexed. GitHub Actions build
> and publish the extension. The index lives in PostgreSQL (pgvector) or Azure AI Search.
> New commits are indexed automatically (push notifications and/or scheduled checks).
> Deploys to Azure Container Apps with no secrets in GitHub. Next: hardening (phase 7). See [docs/DESIGN.md](docs/DESIGN.md) for the full design and roadmap.

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

## Deploy to Azure Container Apps

The recommended way to run the backend in production. `infra/main.bicep` creates, in one
resource group:

| Resource | Purpose |
|---|---|
| Container Apps environment with **api** (public HTTPS) and **worker** | The backend |
| User-assigned **managed identity** | Signs in to everything below, plus Azure DevOps and Azure OpenAI. No passwords or keys in configuration |
| **Azure Database for PostgreSQL** (Flexible Server) | Settings, index (pgvector), queue, chat history. Entra sign-in only, no password |
| **Key Vault** | The extension secret and webhook password, and the key that encrypts stored API keys |
| **Storage account** | The encryption key ring, shared by all replicas |
| **Container registry** | The images, built in Azure by the workflow |

`.env` isn't used in Azure, and GitHub holds no secrets: the workflow signs in with GitHub's OIDC
token, and the apps read their two secrets from Key Vault.

**One-time setup**

1. **Create a deployment app.** In the Azure portal: **Microsoft Entra ID > App registrations > New
   registration** (e.g. `chat-with-code-deploy`). Add a federated credential: **Certificates &
   secrets > Federated credentials > GitHub Actions deploying Azure resources**, with your
   repository, entity type **Environment**, name `azure`.
2. **Give it rights.** On the subscription (or a resource group you created for this), assign the app
   **Owner**, or **Contributor** plus **Role Based Access Control Administrator**. The template
   creates role assignments for the app identity.
3. **Create the GitHub environment** `azure` (**Settings > Environments**) with these variables:

   | Variable | Required | Value |
   |---|---|---|
   | `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` | Yes | The deployment app and where to deploy |
   | `AZURE_RESOURCE_GROUP`, `AZURE_LOCATION` | Yes | e.g. `chat-with-code`, `westeurope` |
   | `ADO_ORGANIZATION` | Yes | The part after `https://dev.azure.com/` |
   | `CHAT_ADMINS` | Recommended | Sign-in emails of admins, comma-separated |
   | `EXTENSION_ORIGIN` | Recommended | `https://<publisher>.gallerycdn.vsassets.io` |
   | `NAME_PREFIX` | No | Prefix for resource names (default `chatcode`) |
   | `POLL_INTERVAL_MINUTES` | No | Check for new commits every N minutes (default 5; 0 = once at start) |
   | `AZURE_OPENAI_RESOURCE_ID` | No | Resource id of your Azure OpenAI resource: the app identity gets **Cognitive Services OpenAI User** on it |
   | `AZURE_SEARCH_RESOURCE_ID` | No | Resource id of an Azure AI Search service, if you use it: the identity gets the roles it needs |
   | `AZURE_OPENAI_ENDPOINT`, `AZURE_OPENAI_CHAT_DEPLOYMENT`, `AZURE_OPENAI_EMBEDDING_DEPLOYMENT` | No | Starting organization defaults (you can also enter them in Azure DevOps) |

4. **Run "Deploy to Azure"** from the Actions tab. It creates the infrastructure, builds the
   images in Azure, creates any missing Key Vault secrets with random values, deploys the apps,
   and waits until the API is healthy. The run summary shows the **service address** and the
   **app identity**.
5. **Let the app identity read your repositories.** In Azure DevOps: **Organization settings >
   Users > Add users**, search for the identity's name (e.g. `chatcode-id`), give it **Basic**
   access and add it to the projects to index.
6. **Package the extension** with the service address (`--backend-url`, or the publish workflow's
   `CWC_BACKEND_URL`), publish it, then put its secret into Key Vault and restart the apps. The
   exact commands are in the run summary.
7. In **Organization settings > Chat with code**, choose **Managed identity** under Sign-in, enter
   your Azure OpenAI endpoint and deployments, and **Test saved settings**.

To deploy a new version, run the workflow again or push a tag like `backend-v1.1.0`. Database
migrations run automatically when the API starts.

Notes:
- The API has public HTTPS ingress, so Azure DevOps push notifications can reach it. The
  service hook password is the `webhook-password` secret in Key Vault (username `azdo-hooks`).
- PostgreSQL accepts connections from Azure services and only Entra sign-in. Private networking
  (VNet integration, private endpoints) can be added later.
- Key Vault has purge protection: after deleting the resource group, a Key Vault with the same
  name can't be created for 30 days. Use another `NAME_PREFIX` if you redeploy from scratch.

## Run the backend with Docker Compose

For local use, testing, or a single server. (For production in Azure, see the previous section.)

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
the background and the page shows progress. Each branch has two buttons:

- **Re-index** picks up changes since the last run (or starts over after a failure). It also
  re-splits, once, files that were indexed with an older version of the code splitter.
- **Rebuild** (with a confirmation) indexes every file of the branch again. It costs the full
  indexing fee for the branch; use it after changing the embedding model or if answers look wrong.

After a branch is indexed, the chat model writes 4–5 **starter questions** from its README and
top-level folders; they appear as clickable suggestions in the empty chat. They are rewritten only
when the README changes (one small model call each time), and the worker fills them in at start
for branches indexed before this existed.

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

### Keeping the index up to date

New commits are picked up two ways. Both feed the same queue, so a push seen both ways is
indexed once.

| Way | How | Needs |
|---|---|---|
| **Scheduled checks** | The worker compares each indexed branch's latest commit with the last indexed one | Nothing. `INDEX_POLL_MINUTES=0` (the current default) checks once when the worker starts; set it to `5` for production |
| **Push notifications** | Azure DevOps calls the service when code is pushed, so changes are indexed within seconds | The service reachable from the internet (Azure DevOps sends them from its own servers), plus `WEBHOOK_USERNAME` and `WEBHOOK_PASSWORD`. Then follow the steps in **Project settings > Chat with code > Keeping the index up to date** to add a **Code pushed** service hook per project |

The same page shows when each repository last sent a push notification, so you can tell whether
they arrive. In Azure Container Apps with external ingress, push notifications work; with
internal-only ingress or IP restrictions, rely on scheduled checks.

### Where the index is kept

| Store | Choose when | Setup |
|---|---|---|
| **PostgreSQL (pgvector)**, the default | You want everything in the bundled database | Nothing extra. Uses 1,536-dimension embeddings. |
| **Azure AI Search** | You already use Azure AI Search, want a managed search service, or want larger embeddings (256–3,072 dimensions) | Create a search service (Basic tier or higher). In **Organization settings > Chat with code > Advanced**, choose Azure AI Search and enter the endpoint, an index name and the admin key. The index is created on the first indexing run. |

With managed identity instead of keys, give the backend's identity the **Search Index Data
Contributor** and **Search Service Contributor** roles on the search service.

Search works the same in both: keyword search (for exact names like `OrderService`) and vector
search (for meaning), merged into one ranking. Switching store, index or embedding model
re-indexes the affected branches automatically and removes them from the old store.

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

Files are split into chunks before embedding, each under about 1,500 tokens. Small files stay
whole. Bigger files are split along their structure, so a function or method is never cut in half
and each chunk is named after it (for example `OrderService.submit`):

| Language | Split by |
|---|---|
| C# | classes and members (Roslyn) |
| TypeScript/JavaScript, Python, Java, Go, Kotlin, Rust, C/C++, PHP, Swift, Scala, Dart, Groovy | classes, functions and methods |
| Markdown | headings |
| Everything else | blank lines |

If a file's structure can't be read reliably, it's split at blank lines instead. When the splitter
improves, already indexed files are re-split once by the next run started by a push notification
or by **Re-index**, never by the scheduled checks.

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

These workflows live in `.github/workflows`:

| Workflow | When | What |
|---|---|---|
| **Backend CI** | Every pull request; pushes to `main` that touch the backend, `infra/`, `deploy/` or a workflow; or **Run workflow** | Builds and runs all backend tests (including the PostgreSQL tests), builds both Docker images, compiles and lints the Bicep templates, and lints the workflows. Nothing is deployed |
| **Extension CI** | Every pull request; pushes to `main` that touch the extension; or **Run workflow** | Unit tests and packaging; the `.vsix` is attached to the run |
| **Deploy to Azure** | **Run workflow**, or a tag like `backend-v1.2.0` | See [Deploy to Azure Container Apps](#deploy-to-azure-container-apps) |
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
| `GET /api/admin/push-notifications` | How new commits are detected (polling interval, whether push notifications are set up) |
| `POST /api/webhooks/ado` | Azure DevOps "Code pushed" service hook (basic auth, not the extension token) |

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
| `AzureDevOps:ServicePrincipal:TenantId` / `ClientId` / `ClientSecret` | Yes, to index (Compose) | — | How the backend reads repositories |
| `AzureDevOps:UseManagedIdentity` | In Azure | false | Read repositories as the app's managed identity instead |
| `Database:UseManagedIdentity` | In Azure | false | Sign in to PostgreSQL with Entra (the connection string has no password) |
| `Security:DataProtectionBlobUri` / `DataProtectionKeyVaultKeyId` | In Azure | — | Keep the encryption key ring in a blob, encrypted with a Key Vault key |
| `AZURE_CLIENT_ID` | In Azure | — | Which user-assigned managed identity to use |
| `ConnectionStrings:Metadata` | Yes | set by compose | PostgreSQL connection |
| `AzureOpenAI:Endpoint`, `ApiKey`, `UseManagedIdentity`, `EmbeddingDeployment`, `ChatDeployment`, `EmbeddingDimensions` | First start | dimensions 1536 | Starting organization defaults (saved once) |
| `VectorStore:Kind`, `AzureSearchEndpoint`, `AzureSearchIndexName`, `AzureSearchApiKey` | First start | `PgVector` | Starting vector store (saved once, then managed in Azure DevOps) |
| `Indexing:MaxFileSizeKb` | No | 500 | Larger files are skipped |
| `Indexing:ExcludeGlobs` | No | — | Extra skip rules for every repository (`.gitignore` syntax) |
| `Indexing:PollingEnabled` / `PollIntervalMinutes` | No | true / 0 | Check branches for new commits; 0 = once at worker start, otherwise every N minutes |
| `Security:WebhookUsername` / `WebhookPassword` | For push notifications | — | Basic-auth credentials Azure DevOps service hooks use (`WEBHOOK_USERNAME`, `WEBHOOK_PASSWORD`) |
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
infra/                                Azure Container Apps deployment (Bicep)
deploy/                               docker compose + .env.example
src/backend/
  ChatWithCode.Api/                   HTTP API: sign-in checks, chat (streaming), conversations
  ChatWithCode.Worker/                indexer (job loop, RepositoryIndexer, index/status commands)
  ChatWithCode.Core/                  options, interfaces, file filtering, chunking, prompts
  ChatWithCode.Data/                  EF Core metadata database, migrations, job queue, settings
  ChatWithCode.AzureDevOps/           Git REST client (service principal) and user checks (user token)
  ChatWithCode.AzureOpenAI/           embeddings and streaming chat
  ChatWithCode.VectorStores.PgVector/ pgvector store with hybrid search
  ChatWithCode.VectorStores.AzureAISearch/ Azure AI Search store (native hybrid search)
  tests/ChatWithCode.Tests/           unit and integration tests
```

## Contributing, security and license

Contributions are welcome as pull requests from forks; see [CONTRIBUTING.md](CONTRIBUTING.md).
Report security problems privately as described in [SECURITY.md](SECURITY.md).
Licensed under the [MIT License](LICENSE).
