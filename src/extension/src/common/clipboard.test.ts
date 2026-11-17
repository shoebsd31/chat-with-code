import { afterEach, describe, expect, it, vi } from "vitest";
import { copyText } from "./clipboard";
import { renderAnswer, renderMermaid } from "./markdown";

function mockClipboard(writeText: (text: string) => Promise<void>) {
  Object.defineProperty(navigator, "clipboard", { value: { writeText }, configurable: true });
}

function mockExecCommand(result: boolean, seen: string[] = []) {
  Object.defineProperty(document, "execCommand", {
    configurable: true,
    value: vi.fn(() => {
      seen.push(document.querySelector("textarea")?.value ?? "");
      return result;
    }),
  });
  return seen;
}

describe("copyText", () => {
  afterEach(() => vi.restoreAllMocks());

  it("uses the clipboard API when it's allowed", async () => {
    const writeText = vi.fn(async () => undefined);
    mockClipboard(writeText);
    expect(await copyText("hello")).toBe(true);
    expect(writeText).toHaveBeenCalledWith("hello");
  });

  it("falls back to a selected textarea when the iframe isn't allowed to write the clipboard", async () => {
    mockClipboard(async () => Promise.reject(new DOMException("Write permission denied.", "NotAllowedError")));
    const seen = mockExecCommand(true);
    expect(await copyText("from the fallback")).toBe(true);
    expect(seen).toEqual(["from the fallback"]);
    expect(document.querySelector("textarea")).toBeNull(); // cleaned up
  });

  it("reports failure when both ways are blocked", async () => {
    mockClipboard(async () => Promise.reject(new Error("blocked")));
    mockExecCommand(false);
    expect(await copyText("x")).toBe(false);
  });
});

describe("diagram copy button", () => {
  it("copies the Mermaid source", async () => {
    const writeText = vi.fn(async () => undefined);
    mockClipboard(writeText);
    const source = "graph TD\n  A-->B";
    document.body.innerHTML = renderAnswer("```mermaid\n" + source + "\n```", new Map());
    await renderMermaid(document.body, false);

    const button = document.querySelector<HTMLButtonElement>(".mermaid-toolbar button");
    expect(button?.textContent).toContain("Copy diagram");
    button!.click();
    await vi.waitFor(() => expect(writeText).toHaveBeenCalledWith(source));
    await vi.waitFor(() => expect(button!.textContent).toContain("Copied"));
  }, 30_000);
});
