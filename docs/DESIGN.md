# Chat with Code for Azure DevOps — Design

Status: **Approved** · Phases 1–6 built · Last updated: 2026-11-16

## 1. Goal

Let people chat with the source code and markdown in their Azure DevOps (ADO) repositories,
from inside Azure DevOps, using **their own Azure OpenAI deployment** (bring your own key)
and **their own vector database**. Answers come only from the indexed repository content
and always cite the files and lines they used.

No GitHub Copilot or Claude is involved. Everything runs on infrastructure the customer
hosts.

## 2. Decisions so far

| Topic | Decision |
|---|---|
| Azure DevOps edition | **ADO Services only** (dev.azure.com). No on-prem Server. |
| Hosting | **Self-hosted** by each customer. Production target: **Azure Container Apps** (phase 8). Docker Compose for local and small installs. |
| Permissions | **Respect ADO repo permissions.** Users can only get answers from repos they can read. |
| Backend | **.NET** (ASP.NET Core API + background worker). |
| Frontend | React 19 + `azure-devops-extension-sdk`, built with Vite. Styled with the theme variables Azure DevOps provides, so it follows light and dark themes. (`azure-devops-ui` requires React 16, which is out of support.) |
| Who can change settings | Emails listed in the backend's `CHAT_ADMINS`. Not Azure DevOps roles: checking those would need a high-privilege extension scope for every user. Everyone else sees settings read-only. |
| Backend address | **Built in when the extension is packaged.** Org-wide extension settings can be changed by any user, so an address stored there could be redirected to collect sign-in tokens. |
| Extension publishing | GitHub Actions: CI on pull requests; publishing on `extension-v*` tags or manually, signing in as an Entra app through GitHub OIDC (no stored secret). |
| Where it appears | A hub **under Repos** ("Ask the Code"). Plus settings pages for admins. |
| API keys | **Org admin sets defaults; project admins can override** for their project. Users never see keys. |
| Model providers (v1) | **Azure OpenAI** only. The provider layer stays pluggable, so OpenAI, OpenRouter and local models can be added later. |
| Vector stores (v1) | **PostgreSQL + pgvector** and **Azure AI Search**. |
| Branches | Default branch always, **plus branches an admin chooses**. |
| Re-indexing | **Automatically on push** (changed files only), plus a manual "Re-index now" button. |
| Chat scope | One conversation can search **one or more repos**. |
| v1 chat features | Clickable file/line citations, saved chat history, Mermaid diagrams in answers. |
| Default chat model | `gpt-6-luna` (admins can change it per organization or project). |
| No answer in the code | Say so ("I couldn't find this in the selected repositories.") and show the closest files. Never answer from general knowledge. |
| Conversation retention | Deleted after **30 days** without new messages (`Chat:RetentionDays`). |
| Not in v1 | ADO Wiki, work items and PRs; providers other than Azure OpenAI; vector stores other than the two above. |

## 3. Architecture

```
┌──────────────── Azure DevOps (user's browser) ────────────────┐
│ Extension (React)                                             │
│  • Repos ▸ Ask the Code   (chat, repo/branch picker, sources) │
│  • Organization settings ▸ Chat with Code   (org defaults)    │
│  • Project settings ▸ Chat with Code   (project override,     │
│    repos & branches to index, index status)                   │
└───────┬───────────────────────────────────────────────────────┘
        │ HTTPS: extension token (who is calling) +
        │        user's ADO access token (what they may read)
        ▼
┌──────────────── Customer-hosted backend ──────────────────────┐
│ ChatWithCode.Api (ASP.NET Core)                               │
│   auth · permission check · chat (SSE stream) · settings ·    │
│   index status · webhook receiver                             │
│        │                         │ enqueue                    │
│        ▼                         ▼                            │
│ Retrieval + chat            ChatWithCode.Worker               │
│ (hybrid search, prompt,     (fetch → filter → chunk → embed   │
│  citations)                  → upsert; incremental by commit) │
│        │                         │                            │
│        ▼                         ▼                            │
│ IChatModel / IEmbedder ──► Azure OpenAI (customer's resource) │
│ IVectorStore ──► pgvector  |  Azure AI Search                 │
│ PostgreSQL (metadata: settings, repos, jobs, chats, usage)    │
└───────────────────────────────────────────────────────────────┘
```

### 3.1 Extension (frontend)

- **Hub:** contribution of type `ms.vss-web.hub`, targeting `ms.vss-code-web.code-hub-group`
  (the Repos group).
- **Org settings page:** targets `ms.vss-web.collection-admin-hub-group`.
- **Project settings page:** targets `ms.vss-web.project-admin-hub-group`.
- **Backend URL:** built into the extension when it's packaged
  (`npm run package -- --backend-url … --publisher …`). It isn't stored in extension data: those
  settings can be changed by any user of the extension, which would let someone redirect
  everyone's tokens to their own server.
- **Calls to the backend send:**
  - `SDK.getAppToken()` as `Authorization: Bearer`: a JWT signed with the extension's secret.
    Its signature proves the request comes from our extension.
  - `SDK.getAccessToken()` as `X-AzureDevOps-Token`: the user's own ADO token. The backend
    never decodes it; see 3.2.
- **Mermaid:** the `mermaid` library is bundled into the extension and renders
  ```` ```mermaid ```` blocks in answers.

### 3.2 API (`ChatWithCode.Api`)

- **Authentication:** validates the extension token (HS256 signature and lifetime, using the
  extension's secret from the Marketplace publisher portal), as Microsoft documents.
- **Who the user is:** Microsoft says Azure DevOps token claims are undocumented and may be
  encrypted, so tokens must be treated as opaque. The backend therefore never reads claims.
  It calls `GET {org}/_apis/connectionData` with the user's token to get the user's id and
  name. Only members of the configured organization get an answer, which also checks the
  organization.
- **Permission trimming:**
  1. Calls `GET {org}/_apis/git/repositories` with the user's token. ADO only returns
     repos the user can read.
  2. Caches the user and their repos for 5 minutes (keyed by a hash of the token).
  3. Rejects a chat request if any selected repo isn't in that list (HTTP 403).
  4. Passes the selected repos and branches as a filter to every search.
- **Admin checks:** the user's sign-in email (from `connectionData`, confirmed by Azure DevOps)
  must be listed in `CHAT_ADMINS`. Reading settings is open to signed-in users; secrets are
  never returned (only "set" and the last four characters).
- **Streaming:** the chat endpoint returns Server-Sent Events. Events are `conversation`,
  `delta`, `citations`, `done` and `error`.
- **Rate limit:** 20 questions per user per minute by default (`Chat:RequestsPerMinute`).
- **CORS:** allows only the extension's hosting origins.

### 3.3 Indexer (`ChatWithCode.Worker`)

```
trigger ─► fetch ─► filter ─► chunk ─► embed ─► upsert ─► record commit
```

**Trigger**
- An ADO **service hook** (`git.push`) posts to `/api/webhooks/ado`, protected with basic
  auth.
- **Polling fallback:** many self-hosted backends aren't reachable from the internet, which
  ADO service hooks need. So the worker also checks branch head commits every few minutes.
  It's configurable and on by default.
- Admins also get a **"Re-index now"** button.

**Fetch**
- Uses the ADO Git REST API (refs, commits, recursive trees, blobs) with the backend's own
  identity: an Entra **service principal** only (decided; no PATs).
- Each run lists the tree at the target commit, with blob ids and sizes. The `indexed_files`
  table remembers which blob id of each file was indexed, so only files whose blob id
  changed are downloaded and embedded, and files no longer in the tree are removed. Renames
  count as a removal plus an addition.
- Progress is saved after every batch of 32 files. A job that fails midway resumes where it
  stopped on retry.

**Filter**
- Skips binaries, vendored and generated folders (`node_modules`, `bin`, `obj`, `dist`,
  …), lockfiles, minified files and source maps, plus files over a configurable size
  (default 500 KB). Content is checked too: files with NUL bytes or invalid UTF-8 are skipped.
- `.gitignore` is **not** used: committed files are by definition not ignored by it. Instead,
  admins can add rules for every repository (`Indexing:ExcludeGlobs`), and each repository can
  add a `.chatwithcodeignore` file (gitignore syntax, `!` re-includes) at its root.

**Chunk**
- **Markdown:** split by headings. Each chunk keeps its heading path.
- **C#:** split by type and member using **Roslyn**.
- **Other languages (v1):** splits at blank lines, merging blocks up to the limit; blocks too
  big on their own are cut into windows with a 3-line overlap. The first declaration
  (class, function, …) names the chunk.
- Files at or under the limit (about 1,500 tokens, counted with the cl100k tokenizer the
  embedding models use) stay whole.
- The embedded text is a short header (file, language, symbol) plus the chunk, which helps
  retrieval. The stored content is the chunk alone.
- Every chunk carries metadata: repo, branch, path, language, start/end line, symbol name,
  commit.
- Later: add tree-sitter for better results in more languages.

**Embed**
- Calls the Azure OpenAI embeddings deployment in batches, with retries and backoff on 429
  errors.
- `text-embedding-3-small` (1,536 dimensions) is the default. `-large` is supported with
  `dimensions` set to 1,536, which keeps it within pgvector's index limits.

**Upsert**
- Writes the chunks to the vector store.
- Deletes the chunks of removed or renamed files.
- Records the indexed commit per repo and branch.

**Embedding model rule**
- Each index records which embedding model and dimension size built it.
- Changing either triggers a full re-index automatically on the next run. The UI will warn
  before you do it.

**Before the settings UI (phase 4)**
- Starting organization defaults come from configuration (`.env`) and are saved once, when
  no organization settings exist yet.
- Repositories are added with a worker command:
  `docker compose run --rm worker index --project P --repo R [--branch B] [--full]`, and
  `status` shows progress.

### 3.4 Retrieval and chat

1. **Rewrite the question:** turn a follow-up into a standalone question, using the chat
   model and recent history.
2. **Hybrid search** over the allowed repos and branches:
   - **pgvector:** HNSW cosine search plus Postgres full-text search (`tsvector`), merged
     with reciprocal rank fusion.
   - **Azure AI Search:** native hybrid query (BM25 over content, symbol and path, plus HNSW
     cosine vectors), merged by the service with reciprocal rank fusion and pre-filtered to the
     allowed branches. Each branch has a filter-safe key (repository id plus a hash of the branch
     name), since branch names can contain any character. Uses `Azure.Search.Documents` 11.7
     (the established 11.x API; 12.0 is a later upgrade). The semantic ranker isn't used yet.
3. **Pick context:** take the top chunks, merge neighbouring chunks from the same file and
   fit them within a token budget (default 8,000 tokens).
4. **Prompt:** answer only from the sources, cite them as `[1]`, `[2]`…, and say clearly when
   the answer isn't in the code. Repo content is wrapped as data that the model is told not
   to follow as instructions, to limit prompt injection from code comments.
5. **Stream** the answer, then send the citation list: the sources the answer cited, or, when
   it cited none, the top 5 as "closest files". If search finds nothing at all, the model
   isn't called. Each citation links to
   `https://dev.azure.com/{org}/{project}/_git/{repo}?path={path}&version=GB{branch}&line={start}&lineEnd={end}&lineStartColumn=1&lineEndColumn=1`.
6. **Save** the conversation, messages, citations and token usage.

### 3.5 Pluggable interfaces

```csharp
public interface IChatModel     // v1: AzureOpenAIChatModel
{
    IAsyncEnumerable<ChatDelta> StreamAsync(ChatRequest request, CancellationToken ct);
}

public interface IEmbedder      // v1: AzureOpenAIEmbedder
{
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct);
    string ModelId { get; }
    int Dimensions { get; }
}

public interface IVectorStore   // v1: PgVectorStore, AzureAISearchStore
{
    Task EnsureIndexAsync(IndexSpec spec, CancellationToken ct);
    Task UpsertAsync(IReadOnlyList<CodeChunk> chunks, CancellationToken ct);
    Task DeleteFilesAsync(RepoBranch target, IReadOnlyList<string> paths, CancellationToken ct);
    Task<IReadOnlyList<SearchHit>> HybridSearchAsync(SearchQuery query, CancellationToken ct);
}
```

The Azure OpenAI adapters use the official `Azure.AI.OpenAI` SDK. Chat requests send
`max_completion_tokens` (not the legacy `max_tokens`) and no `temperature`, because reasoning
models such as GPT-6 reject both. Later providers (OpenAI,
OpenRouter, Ollama/vLLM) can share one OpenAI-compatible adapter.

### 3.6 Settings and keys

- **Inheritance:** each setting is resolved project override first, then the org default.
  This covers the Azure OpenAI endpoint, chat deployment, embedding deployment, API key,
  vector store and its connection.
- **Key storage:** keys are encrypted at rest in Postgres using ASP.NET Core Data
  Protection. The key ring is stored on a mounted volume or in Azure Key Vault.
- **Managed identity:** Azure OpenAI and Azure AI Search can use **managed identity**
  instead of API keys when the backend runs in Azure.
- **Keys never leave the server.** The API returns only "set ✓ (…last 4 characters)".

### 3.7 Data model (PostgreSQL)

| Table | Purpose |
|---|---|
| `organizations` | ADO org id/name, backend config |
| `settings` | scope (org/project), provider + vector store config, encrypted secrets |
| `repositories` | ADO repo id, project, name, default branch, enabled |
| `indexed_branches` | repo, branch, last indexed commit, status, counts, last error |
| `index_jobs` | queue: repo/branch, type (full/incremental), state, attempts, timestamps |
| `conversations` | user id, title, selected repos/branches, created/updated |
| `messages` | conversation, role, content, model, token counts |
| `citations` | message, repo, branch, path, line range, score |
| `indexed_files` | per branch: path, blob id last indexed, chunk count, skip reason |
| `code_chunks` (pgvector only) | metadata + `embedding vector(1536)` (HNSW, cosine) + generated `search_vector tsvector` over path, symbol and content (GIN, `simple` config) |

The job queue uses Postgres (`SELECT … FOR UPDATE SKIP LOCKED`), so self-hosting needs no
separate queue service.

### 3.8 Security

- Every search is filtered by organization **and** by the user's readable repos.
- Prompts and code are not logged by default. Only token counts and timings are.
- Secrets are encrypted at rest. The backend's ADO identity needs only **Code (Read)**.
- Webhooks are protected with basic auth. The API is CORS-restricted and rate-limited per
  user.

## 4. Repository layout

```
docs/DESIGN.md
src/backend/
  ChatWithCode.sln
  ChatWithCode.Api/                 ASP.NET Core API
  ChatWithCode.Worker/              indexer background service
  ChatWithCode.Core/                domain, interfaces, chunking, retrieval, prompts
  ChatWithCode.AzureDevOps/         ADO REST client, token validation, permissions
  ChatWithCode.AzureOpenAI/         IChatModel + IEmbedder
  ChatWithCode.VectorStores.PgVector/
  ChatWithCode.VectorStores.AzureAISearch/
  ChatWithCode.Data/                EF Core + migrations (metadata DB)
  tests/                            unit + integration tests
src/extension/
  vss-extension.json                manifest
  src/hub/                          Ask the Code
  src/settings-org/  src/settings-project/
deploy/
  docker-compose.yml                api + worker + postgres(pgvector)
  .env.example
README.md                           setup guide
```

## 5. Delivery phases

| # | Phase | What you can see at the end |
|---|---|---|
| 0 | **This design** | Approved doc |
| 1 | Backend skeleton | Solution, config, metadata DB + migrations, health endpoint, docker compose with pgvector |
| 2 | Indexer | Index an ADO repo into pgvector (full + incremental), with unit tests for filtering and chunking |
| 3 | Chat API | Hybrid retrieval, prompt, streaming answers with citations, saved history |
| 4a | Extension: chat | "Chat with code" hub (repo picker, streaming chat, sources, Mermaid, history), connection test page, packaging, GitHub Actions publishing |
| 4b | Extension: admin settings | Organization and project model settings; which repositories and branches to index; re-index button |
| 5 | Azure AI Search | Second vector store: index created automatically, hybrid search, settings form, clean-up of the old store when switching |
| 6 | Auto re-index | "Code pushed" service hook endpoint (basic auth, set up by admins with guided steps), scheduled checks (0 = once at start, configurable), last-notification status |
| 7 | Hardening | Tests, README setup guide, how to publish the extension privately |
| 8 | Azure Container Apps | Bicep template: Container Apps (API + worker), Azure Database for PostgreSQL Flexible Server with pgvector, encryption key ring in Blob Storage protected by Key Vault, managed identity for Azure OpenAI, HTTPS ingress |

## 6. Testing limits in this environment

The coding environment has no access to a real ADO organization, Azure OpenAI or Azure AI
Search. I'll unit test the logic (filtering, chunking, permission trimming, prompt building,
rank fusion) and integration test pgvector locally where Docker allows. Testing end to end
against ADO and Azure needs your organization and keys, using the setup guide from phase 7.
The .NET SDK isn't installed in this environment yet and will be installed in phase 1.

## 7. Open points (sensible defaults chosen; tell me if you disagree)

1. **Secret storage default:** ASP.NET Data Protection in Postgres, with Azure Key Vault as
   an option.
2. **Backend's ADO identity:** decided: **service principal only**.
3. **.NET version:** .NET 10 (LTS).
4. **Extension publishing:** a private Marketplace extension shared with your organization.
   You'll need a Marketplace publisher account.
