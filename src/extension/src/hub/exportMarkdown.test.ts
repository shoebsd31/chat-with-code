import { describe, expect, it } from "vitest";
import type { Citation, Repository } from "../common/api";
import { conversationMarkdown, exportFileName } from "./exportMarkdown";
import type { ChatItem } from "./MessageView";

const cite = (number: number, path: string, url: string | null = `https://dev.azure.com/x/${number}`): Citation => ({
  number, path, url, repositoryId: "r1", project: "Fabrikam", repository: "web-app", branch: "refs/heads/main",
  startLine: 10, endLine: 20, symbol: "OrderService.SubmitAsync", used: true,
});

const repositories: Repository[] = [{ id: "r1", projectId: "p", project: "Fabrikam", name: "web-app", branches: [] }];

const items: ChatItem[] = [
  { key: "q1", role: "user", content: "How are orders saved?", citations: [], state: "done" },
  {
    key: "a1",
    role: "assistant",
    content: "Orders are saved by `SaveAsync` [1], see [2].\n\n```mermaid\ngraph TD; A[1]-->B\n```\n\nNot a citation: `list[1]`.",
    citations: [cite(1, "src/OrderService.cs"), cite(2, "docs/orders.md", null)],
    answerFound: true,
    state: "done",
  },
];

describe("conversationMarkdown", () => {
  const md = conversationMarkdown({ title: "How are orders saved?", items, repositories, targets: [{ repositoryId: "r1", branch: "refs/heads/main" }], exportedAt: new Date(2026, 10, 17) });

  it("has the title, what was searched, and the question", () => {
    expect(md).toMatch(/^# How are orders saved\?\n/);
    expect(md).toContain("Searched: web-app (main)");
    expect(md).toContain("## How are orders saved?");
  });

  it("links citation markers outside code, and keeps diagrams as Mermaid", () => {
    expect(md).toContain("`SaveAsync` [[1]](https://dev.azure.com/x/1)");
    expect(md).toContain("see [2]."); // no link for this source
    expect(md).toContain("```mermaid\ngraph TD; A[1]-->B\n```");
    expect(md).toContain("`list[1]`");
  });

  it("lists the sources with lines, symbol and repository", () => {
    expect(md).toContain("1. [src/OrderService.cs, lines 10–20](https://dev.azure.com/x/1) — `OrderService.SubmitAsync` · web-app · main");
    expect(md).toContain("2. docs/orders.md, lines 10–20 —");
  });

  it("labels the closest files when the answer wasn't found", () => {
    const notFound = conversationMarkdown({
      title: "x", repositories, targets: [],
      items: [items[0], { ...items[1], answerFound: false }],
    });
    expect(notFound).toContain("**Closest files** (the answer wasn't found in them)");
  });
});

describe("exportFileName", () => {
  it.each([
    ["How are orders saved?", "how-are-orders-saved.md"],
    ["", "conversation.md"],
    ["Ünïcode / ✓ only", "n-code-only.md"],
  ])("%s → %s", (title, name) => expect(exportFileName(title)).toBe(name));
});
