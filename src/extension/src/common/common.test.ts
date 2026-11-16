import { describe, expect, it, vi } from "vitest";
import { ApiError, createApi, type Citation, type ChatEvent } from "./api";
import type { Host } from "./host";
import { renderAnswer } from "./markdown";
import { SseParser } from "./sse";

const host: Host = {
  context: { organization: "contoso", userName: "Ada" },
  getAppToken: async () => "app-token",
  getAccessToken: async () => "user-token",
  loaded: () => undefined,
  isDevelopment: false,
};

const citation: Citation = {
  number: 1, repositoryId: "r1", project: "Fabrikam", repository: "web-app", branch: "refs/heads/main",
  path: "src/OrderService.cs", startLine: 10, endLine: 20, symbol: "OrderService", url: "https://dev.azure.com/contoso/x?line=10", used: true,
};

describe("SseParser", () => {
  it("parses events split across chunks and line endings", () => {
    const parser = new SseParser();
    expect(parser.feed('event: delta\r\ndata: {"text":"Hel')).toEqual([]);
    expect(parser.feed('lo"}\r\n\r\n: keep-alive\n\nevent: done\ndata: {}\n\n')).toEqual([
      { event: "delta", data: '{"text":"Hello"}' },
      { event: "done", data: "{}" },
    ]);
  });

  it("joins multi-line data", () => {
    expect(new SseParser().feed("data: a\ndata: b\n\n")).toEqual([{ event: "message", data: "a\nb" }]);
  });
});

describe("renderAnswer", () => {
  it("turns known citations into links to the lines", () => {
    const html = renderAnswer("Submit validates ids [1].", new Map([[1, citation]]));
    expect(html).toContain('<a class="cite" href="https://dev.azure.com/contoso/x?line=10" target="_blank"');
    expect(html).toContain(">1</a>");
  });

  it("shows unknown citations as plain chips while streaming", () => {
    expect(renderAnswer("See [2].", new Map())).toContain('<span class="cite" aria-label="Source 2">2</span>');
  });

  it("does not treat brackets inside code as citations", () => {
    const html = renderAnswer("Use `items[0]` here.", new Map([[0, citation]]));
    expect(html).toContain("<code>items[0]</code>");
  });

  it("highlights code and keeps mermaid as a placeholder", () => {
    const html = renderAnswer("```csharp\nvar x = 1;\n```\n\n```mermaid\ngraph TD; A-->B\n```", new Map());
    expect(html).toContain('class="hljs language-csharp"');
    expect(html).toContain('<div class="mermaid-block"><pre><code>graph TD; A--&gt;B</code></pre></div>');
  });

  it("keeps the diagram source readable for the renderer", () => {
    document.body.innerHTML = renderAnswer("```mermaid\nsequenceDiagram\n  A->>B: call\n  B-->>A: done\n```", new Map());
    expect(document.querySelector(".mermaid-block code")?.textContent).toBe("sequenceDiagram\n  A->>B: call\n  B-->>A: done");
  });

  it("removes scripts and event handlers from model output", () => {
    const html = renderAnswer('<img src=x onerror="alert(1)"><script>alert(2)</script>[click](javascript:alert(3))', new Map());
    expect(html).not.toContain("onerror");
    expect(html).not.toContain("<script");
    expect(html).not.toContain("javascript:");
  });

  it("opens links in a new tab", () => {
    expect(renderAnswer("[docs](https://learn.microsoft.com)", new Map())).toContain('target="_blank" rel="noopener noreferrer"');
  });
});

describe("createApi", () => {
  it("sends both tokens and reads problem details", async () => {
    const fetchImpl = vi.fn(async () => new Response(JSON.stringify({ title: "Can't answer this request", detail: "Pick at least one repository to search." }), { status: 400 }));
    const api = createApi(host, "https://chat.example.com/", fetchImpl);

    const error = await api.repositories().catch((e: unknown) => e);

    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).message).toBe("Pick at least one repository to search.");
    const [url, init] = fetchImpl.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe("https://chat.example.com/api/repositories");
    expect(init.headers).toMatchObject({ Authorization: "Bearer app-token", "X-AzureDevOps-Token": "user-token" });
  });

  it("explains when the service can't be reached", async () => {
    const api = createApi(host, "https://chat.example.com", async () => {
      throw new TypeError("Failed to fetch");
    });
    await expect(api.me()).rejects.toThrow("Can't reach the Chat with code service at https://chat.example.com");
  });

  it("refuses to run without a packaged backend address", async () => {
    await expect(createApi(host, "").me()).rejects.toThrow("packaged without a backend address");
  });

  it("streams chat events", async () => {
    const body = 'event: conversation\ndata: {"conversationId":"c1","title":"t","question":"q"}\n\nevent: delta\ndata: {"text":"Hi"}\n\nevent: done\ndata: {"messageId":"m1","promptTokens":1,"completionTokens":2}\n\n';
    const api = createApi(host, "https://chat.example.com", async () => new Response(body, { status: 200, headers: { "Content-Type": "text/event-stream" } }));
    const events: ChatEvent[] = [];

    await api.ask({ question: "q", targets: [{ repositoryId: "r1", branch: "refs/heads/main" }] }, (e) => events.push(e));

    expect(events.map((e) => e.type)).toEqual(["conversation", "delta", "done"]);
    expect(events[1]).toEqual({ type: "delta", text: "Hi" });
  });
});
