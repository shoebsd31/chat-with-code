import type { Host } from "./host";
import { SseParser } from "./sse";

declare const __BACKEND_URL__: string;

/** Built in when the extension is packaged. */
export const BackendUrl: string = __BACKEND_URL__;

// ---- Backend contracts (mirror ChatWithCode.Api/Chat/ChatContracts.cs) ----

export interface ChatTarget {
  repositoryId: string;
  branch: string;
}

export interface Citation {
  number: number;
  repositoryId: string;
  project: string | null;
  repository: string | null;
  branch: string;
  path: string;
  startLine: number;
  endLine: number;
  symbol: string | null;
  url: string | null;
  used: boolean;
}

export interface Branch {
  name: string;
  isDefault: boolean;
  status: "NotIndexed" | "Queued" | "Indexing" | "Ready" | "Failed";
  lastIndexedAt: string | null;
  fileCount: number;
  progressDone: number;
  progressTotal: number;
}

export interface Repository {
  id: string;
  projectId: string;
  project: string;
  name: string;
  branches: Branch[];
}

export interface ConversationSummary {
  id: string;
  title: string;
  updatedAt: string;
  targets: ChatTarget[];
}

export interface Message {
  id: string;
  role: "User" | "Assistant";
  content: string;
  createdAt: string;
  citations: Citation[];
}

export interface Conversation extends Omit<ConversationSummary, "updatedAt"> {
  createdAt: string;
  updatedAt: string;
  messages: Message[];
}

export interface ServiceInfo {
  name: string;
  version: string;
  organization: string;
  extensionConnected: boolean;
}

export interface Me {
  id: string;
  displayName: string;
}

export type ChatEvent =
  | { type: "conversation"; conversationId: string; title: string; question: string }
  | { type: "delta"; text: string }
  | { type: "citations"; citations: Citation[]; answerFound: boolean }
  | { type: "done"; messageId: string; promptTokens: number | null; completionTokens: number | null }
  | { type: "error"; message: string };

export interface AskRequest {
  question: string;
  conversationId?: string;
  targets?: ChatTarget[];
}

/** A failed call, with a message written for the person using the page. */
export class ApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
    readonly title = "Something went wrong",
  ) {
    super(message);
  }
}

export type FetchLike = (input: string, init?: RequestInit) => Promise<Response>;

export interface Api {
  baseUrl: string;
  info(): Promise<ServiceInfo>;
  me(): Promise<Me>;
  repositories(): Promise<Repository[]>;
  conversations(): Promise<ConversationSummary[]>;
  conversation(id: string): Promise<Conversation>;
  deleteConversation(id: string): Promise<void>;
  /** Ask a question; events arrive through onEvent until the stream ends. */
  ask(request: AskRequest, onEvent: (event: ChatEvent) => void, signal?: AbortSignal): Promise<void>;
}

export function createApi(host: Host, baseUrl: string = BackendUrl, fetchImpl: FetchLike = (i, init) => fetch(i, init)): Api {
  const root = baseUrl.replace(/\/+$/, "");

  async function headers(anonymous = false): Promise<Record<string, string>> {
    if (anonymous) return { Accept: "application/json" };
    const [appToken, accessToken] = await Promise.all([host.getAppToken(), host.getAccessToken()]);
    return { Accept: "application/json", Authorization: `Bearer ${appToken}`, "X-AzureDevOps-Token": accessToken };
  }

  async function send(path: string, init: RequestInit = {}, anonymous = false): Promise<Response> {
    if (!root) {
      throw new ApiError("This copy of the extension was packaged without a backend address. Ask your admin to package it with --backend-url.", 0, "Not set up");
    }
    let response: Response;
    try {
      response = await fetchImpl(`${root}${path}`, { ...init, headers: { ...(await headers(anonymous)), ...(init.headers ?? {}) } });
    } catch (error) {
      if ((error as Error).name === "AbortError") throw error;
      throw new ApiError(
        `Can't reach the Chat with code service at ${root}. It may be down, or blocked by a network or browser (CORS) setting. If this keeps happening, tell your admin.`,
        0,
        "Can't connect",
      );
    }
    if (!response.ok) throw await toError(response);
    return response;
  }

  const getJson = async <T>(path: string, anonymous = false) => (await send(path, {}, anonymous)).json() as Promise<T>;

  return {
    baseUrl: root,
    info: () => getJson<ServiceInfo>("/api/info", true),
    me: () => getJson<Me>("/api/me"),
    repositories: () => getJson<Repository[]>("/api/repositories"),
    conversations: () => getJson<ConversationSummary[]>("/api/conversations"),
    conversation: (id) => getJson<Conversation>(`/api/conversations/${encodeURIComponent(id)}`),
    deleteConversation: async (id) => {
      await send(`/api/conversations/${encodeURIComponent(id)}`, { method: "DELETE" });
    },
    ask: async (request, onEvent, signal) => {
      const response = await send("/api/chat", {
        method: "POST",
        headers: { "Content-Type": "application/json", Accept: "text/event-stream" },
        body: JSON.stringify(request),
        signal,
      });
      if (!response.body) throw new ApiError("The service sent an empty answer.", response.status);

      const reader = response.body.pipeThrough(new TextDecoderStream()).getReader();
      const parser = new SseParser();
      for (;;) {
        const { value, done } = await reader.read();
        if (done) break;
        for (const message of parser.feed(value)) {
          onEvent({ type: message.event, ...JSON.parse(message.data) } as ChatEvent);
        }
      }
    },
  };
}

async function toError(response: Response): Promise<ApiError> {
  let detail: string | undefined;
  let title: string | undefined;
  try {
    const problem = (await response.json()) as { detail?: string; title?: string };
    detail = problem.detail;
    title = problem.title;
  } catch {
    // Not a problem+json body.
  }
  const fallback =
    response.status === 401
      ? "You're not signed in to the Chat with code service. Reload the page."
      : response.status >= 500
        ? "The Chat with code service had a problem. Try again in a moment."
        : `The request failed (${response.status}).`;
  return new ApiError(detail ?? fallback, response.status, title);
}
