import type { AdminRepository, Api, ChatEvent, Citation, Conversation, ConversationSummary, ModelSettings, Repository, SaveSettings } from "../common/api";

/**
 * Local development only: an in-browser stand-in for the backend with sample data, so the
 * screens can be built and reviewed without Azure DevOps or a running service.
 */
const repositories: Repository[] = [
  {
    id: "r1",
    projectId: "p1",
    project: "Fabrikam",
    name: "web-app",
    branches: [
      { name: "refs/heads/main", isDefault: true, status: "Ready", lastIndexedAt: new Date().toISOString(), fileCount: 412, progressDone: 0, progressTotal: 0 },
      { name: "refs/heads/release/2.0", isDefault: false, status: "Indexing", lastIndexedAt: null, fileCount: 0, progressDone: 128, progressTotal: 398 },
    ],
  },
  {
    id: "r2",
    projectId: "p1",
    project: "Fabrikam",
    name: "payments-service",
    branches: [{ name: "refs/heads/main", isDefault: true, status: "Ready", lastIndexedAt: new Date().toISOString(), fileCount: 188, progressDone: 0, progressTotal: 0 }],
  },
  {
    id: "r3",
    projectId: "p2",
    project: "Platform",
    name: "docs",
    branches: [{ name: "refs/heads/main", isDefault: true, status: "Failed", lastIndexedAt: null, fileCount: 0, progressDone: 0, progressTotal: 0 }],
  },
];

const citations: Citation[] = [
  cite(1, "src/Orders/OrderService.cs", 18, 44, "Fabrikam.Orders.OrderService.SubmitAsync"),
  cite(2, "src/Orders/OrderRepository.cs", 30, 52, "Fabrikam.Orders.OrderRepository.SaveAsync"),
  cite(3, "docs/architecture.md", 1, 24, "Architecture > Orders"),
];

const answer = `Orders are saved by **\`OrderRepository.SaveAsync\`**, which \`OrderService.SubmitAsync\` calls after validating the order [1].

1. \`SubmitAsync\` checks the basket and reserves stock [1].
2. It calls \`SaveAsync\`, which writes the order and its lines in one transaction using EF Core [2]:

\`\`\`csharp
await using var tx = await _db.Database.BeginTransactionAsync(ct);
_db.Orders.Add(order);
await _db.SaveChangesAsync(ct);
await tx.CommitAsync(ct);
\`\`\`

3. An \`OrderSubmitted\` event is then published for the payments service [3].

\`\`\`mermaid
sequenceDiagram
    participant API as OrdersController
    participant S as OrderService
    participant R as OrderRepository
    participant B as Service Bus
    API->>S: SubmitAsync(order)
    S->>R: SaveAsync(order)
    R-->>S: saved
    S->>B: publish OrderSubmitted
\`\`\`
`;

let conversations: Conversation[] = [
  {
    id: "c1",
    title: "Where is the retry policy configured?",
    createdAt: new Date(Date.now() - 3_600_000).toISOString(),
    updatedAt: new Date(Date.now() - 3_600_000).toISOString(),
    targets: [{ repositoryId: "r2", branch: "refs/heads/main" }],
    messages: [],
  },
  {
    id: "c2",
    title: "How does login work?",
    createdAt: new Date(Date.now() - 90_000_000).toISOString(),
    updatedAt: new Date(Date.now() - 90_000_000).toISOString(),
    targets: [{ repositoryId: "r1", branch: "refs/heads/main" }],
    messages: [],
  },
];

const empty = { isSet: false, hint: null };
let orgSettings: ModelSettings = {
  azureOpenAIEndpoint: "https://contoso-ai.openai.azure.com/",
  chatDeployment: "gpt-6-luna",
  embeddingDeployment: "text-embedding-3-small",
  embeddingDimensions: 1536,
  useManagedIdentity: false,
  apiKey: { isSet: true, hint: "…9f2c" },
  vectorStore: "PgVector",
  azureSearchEndpoint: null,
  azureSearchIndexName: null,
  azureSearchApiKey: empty,
  updatedAt: new Date(Date.now() - 86_400_000).toISOString(),
  updatedBy: "ada@contoso.com",
};
let projectSettings: ModelSettings = { ...Object.fromEntries(Object.keys(orgSettings).map((k) => [k, null])), apiKey: empty, azureSearchApiKey: empty } as ModelSettings;

let adminRepos: AdminRepository[] = [
  {
    id: "r1", name: "web-app", defaultBranch: "refs/heads/main", indexed: true, lastPushNotificationAt: new Date(Date.now() - 180_000).toISOString(),
    branches: [
      { name: "refs/heads/main", isDefault: true, status: "Ready", progressDone: 412, progressTotal: 412, fileCount: 412, chunkCount: 2380, lastIndexedAt: new Date(Date.now() - 600_000).toISOString(), lastError: null },
      { name: "refs/heads/release/2.0", isDefault: false, status: "Indexing", progressDone: 128, progressTotal: 398, fileCount: 0, chunkCount: 0, lastIndexedAt: null, lastError: null },
    ],
  },
  {
    id: "r2", name: "payments-service", defaultBranch: "refs/heads/main", indexed: true, lastPushNotificationAt: null,
    branches: [{ name: "refs/heads/main", isDefault: true, status: "Failed", progressDone: 0, progressTotal: 0, fileCount: 0, chunkCount: 0, lastIndexedAt: null, lastError: "Azure OpenAI refused the request. Check the API key." }],
  },
  { id: "r4", name: "infrastructure", defaultBranch: "refs/heads/main", indexed: false, branches: [], lastPushNotificationAt: null },
];

const apply = (current: ModelSettings, v: SaveSettings): ModelSettings => ({
  ...current,
  ...v,
  apiKey: v.apiKey === null ? current.apiKey : v.apiKey === "" ? empty : { isSet: true, hint: `…${v.apiKey.slice(-4)}` },
  azureSearchApiKey: v.azureSearchApiKey === null ? current.azureSearchApiKey : v.azureSearchApiKey === "" ? empty : { isSet: true, hint: `…${v.azureSearchApiKey.slice(-4)}` },
  updatedAt: new Date().toISOString(),
  updatedBy: "ada@contoso.com",
});

export function createMockApi(): Api {
  const isAdmin = new URLSearchParams(window.location.search).get("admin") !== "false";
  const response = (projectId?: string) => ({ organization: orgSettings, project: projectId ? projectSettings : null, canEdit: isAdmin, problem: null });
  return {
    baseUrl: "https://chat.contoso.example (development stand-in)",
    info: async () => ({ name: "Chat with Code", version: "dev", organization: "contoso", extensionConnected: true }),
    me: async () => ({ id: "u1", displayName: "Ada Lovelace", isAdmin }),
    admin: {
      settings: async (projectId) => delay(250).then(() => response(projectId)),
      saveSettings: async (values, projectId) => {
        await delay(400);
        if (projectId) projectSettings = apply(projectSettings, values);
        else orgSettings = apply(orgSettings, values);
        return { settings: response(projectId), reindexQueued: 0 };
      },
      removeProjectSettings: async () => {
        projectSettings = { ...Object.fromEntries(Object.keys(orgSettings).map((k) => [k, null])), apiKey: empty, azureSearchApiKey: empty } as ModelSettings;
      },
      testSettings: async () => {
        await delay(900);
        return {
          embedding: { ok: true, message: "text-embedding-3-small returned a 1536-dimension vector." },
          chat: { ok: true, message: "gpt-6-luna answered (2 characters)." },
        };
      },
      repositories: async () => delay(250).then(() => adminRepos),
      branches: async () => delay(200).then(() => ["refs/heads/main", "refs/heads/release/2.0", "refs/heads/feature/checkout-v2"]),
      setBranches: async (repositoryId, branches) => {
        await delay(400);
        adminRepos = adminRepos.map((r) =>
          r.id !== repositoryId
            ? r
            : {
                ...r,
                indexed: branches.length > 0,
                branches: branches.map((name) => r.branches.find((b) => b.name === name) ?? { name, isDefault: name === r.defaultBranch, status: "Queued", progressDone: 0, progressTotal: 0, fileCount: 0, chunkCount: 0, lastIndexedAt: null, lastError: null }),
              },
        );
        return adminRepos.find((r) => r.id === repositoryId)!;
      },
      pushSetup: async () => ({ webhookConfigured: true, webhookUsername: isAdmin ? "azdo-hooks" : null, webhookPath: "/api/webhooks/ado", pollingEnabled: true, pollIntervalMinutes: 0 }),
      reindex: async (repositoryId, branch) => {
        adminRepos = adminRepos.map((r) => (r.id !== repositoryId ? r : { ...r, branches: r.branches.map((b) => (b.name === branch ? { ...b, status: "Queued", lastError: null } : b)) }));
      },
    },
    repositories: async () => delay(300).then(() => repositories),
    conversations: async () => conversations.map(({ id, title, updatedAt, targets }): ConversationSummary => ({ id, title, updatedAt, targets })),
    conversation: async (id) => conversations.find((c) => c.id === id) ?? Promise.reject(new Error("That conversation doesn't exist anymore.")),
    deleteConversation: async (id) => {
      conversations = conversations.filter((c) => c.id !== id);
    },
    ask: async (request, onEvent, signal) => {
      const id = request.conversationId ?? crypto.randomUUID();
      const emit = (e: ChatEvent) => onEvent(e);
      emit({ type: "conversation", conversationId: id, title: request.question.slice(0, 80), question: request.question });
      await delay(600, signal);
      const found = !/payroll/i.test(request.question);
      const text = found ? answer : "I couldn't find this in the selected repositories. The closest sources cover order submission [1] and the architecture overview [3].";
      for (const piece of text.match(/[\s\S]{1,12}/g) ?? []) {
        await delay(15, signal);
        emit({ type: "delta", text: piece });
      }
      emit({ type: "citations", citations: found ? citations : citations.map((c) => ({ ...c, used: false })), answerFound: found });
      emit({ type: "done", messageId: crypto.randomUUID(), promptTokens: 2100, completionTokens: 240 });
      if (!conversations.some((c) => c.id === id)) {
        const now = new Date().toISOString();
        conversations = [{ id, title: request.question.slice(0, 80), createdAt: now, updatedAt: now, targets: request.targets ?? [], messages: [] }, ...conversations];
      }
    },
  };
}

function cite(number: number, path: string, startLine: number, endLine: number, symbol: string): Citation {
  return {
    number,
    repositoryId: "r1",
    project: "Fabrikam",
    repository: "web-app",
    branch: "refs/heads/main",
    path,
    startLine,
    endLine,
    symbol,
    url: `https://dev.azure.com/contoso/Fabrikam/_git/web-app?path=/${path}&version=GBmain&line=${startLine}&lineEnd=${endLine + 1}`,
    used: true,
  };
}

function delay(ms: number, signal?: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(resolve, ms);
    signal?.addEventListener("abort", () => {
      clearTimeout(timer);
      reject(new DOMException("Aborted", "AbortError"));
    });
  });
}
