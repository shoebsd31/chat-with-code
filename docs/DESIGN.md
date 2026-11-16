# Chat with Code for Azure DevOps — Design

Status: **Approved** · Phases 1–2 built · Last updated: 2026-11-16

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
| Hosting | **Self-hosted** by each customer (docker compose first). |
| Permissions | **Respect ADO repo permissions.** Users can only get answers from repos they can read. |
| Backend | **.NET** (ASP.NET Core API + background worker). |
| Frontend | React + `azure-devops-extension-sdk` + `azure-devops-ui`, so it looks native to ADO. |
| Where it appears | A hub **under Repos** ("Ask the Code"). Plus settings pages for admins. |
| API keys | **Org admin sets defaults; project admins can override** for their project. Users never see keys. |
| Model providers (v1) | **Azure OpenAI** only. The provider layer stays pluggable, so OpenAI, OpenRouter and local models can be added later. |
| Vector stores (v1) | **PostgreSQL + pgvector** and **Azure AI Search**. |
| Branches | Default branch always, **plus branches an admin chooses**. |
| Re-indexing | **Automatically on push** (changed files only), plus a manual "Re-index now" button. |
| Chat scope | One conversation can search **one or more repos**. |
| v1 chat features | Clickable file/line citations, saved chat history, Mermaid diagrams in answers. |
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
- **Backend URL:** each customer self-hosts, so the backend URL is set once per organization
  and stored in the extension's data service. It isn't a secret.
- **Calls to the backend send:**
  - `SDK.getAppToken()`: a JWT signed with the extension's secret. It proves the request
    comes from our extension and says who the user is.
  - `SDK.getAccessToken()`: the user's own ADO token. The backend uses it only to check
    which repos the user can read.
- **Mermaid:** the `mermaid` library is bundled into the extension and renders
  ```` ```mermaid ```` blocks in answers.

### 3.2 API (`ChatWithCode.Api`)

- **Authentication:** validates the extension token (HS256, using the extension's secret
  from the Marketplace publisher portal) and checks that the organization matches the
  configured one.
- **Permission trimming:**
  1. Calls `GET /{project}/_apis/git/repositories` with the user's token. ADO only returns
     repos the user can read.
  2. Caches the result per user for 5 minutes.
  3. Intersects it with the repos the user selected.
  4. Passes the remaining repos as a filter to every vector search.
- **Admin checks:**
  - Org settings: only members of *Project Collection Administrators*.
  - Project settings: only *Project Administrators*.
  - Both are verified through ADO with the user's token.
- **Streaming:** the chat endpoint returns Server-Sent Events. Events are `token`,
  `citations`, `done` and `error`.
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
   - **Azure AI Search:** native hybrid query (vector + BM25), plus the semantic ranker if
     the customer has it enabled.
3. **Pick context:** take the top chunks, merge neighbouring chunks from the same file and
   fit them within a token budget (default 8,000 tokens).
4. **Prompt:** answer only from the sources, cite them as `[1]`, `[2]`…, and say clearly when
   the answer isn't in the code. Repo content is wrapped as data that the model is told not
   to follow as instructions, to limit prompt injection from code comments.
5. **Stream** the answer, then send the citation list. Each citation links to
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

The Azure OpenAI adapters use the official `Azure.AI.OpenAI` SDK. Later providers (OpenAI,
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
| 4 | Extension | Ask the Code hub (repo picker, chat, sources, Mermaid) and both settings pages |
| 5 | Azure AI Search | Second vector store adapter |
| 6 | Auto re-index | Service hook receiver, polling fallback, branch selection, index status UI |
| 7 | Hardening | Tests, rate limits, README setup guide, how to publish the extension privately |

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
