import type { ChatTarget, Citation, Repository } from "../common/api";
import type { ChatItem } from "./MessageView";
import { shortBranch } from "./RepoPicker";

export interface ExportInput {
  title: string;
  items: ChatItem[];
  repositories: Repository[];
  targets: ChatTarget[];
  exportedAt?: Date;
}

/**
 * A conversation as one Markdown document: questions and answers in order, [n] markers linked to
 * the cited lines in Azure DevOps, a source list under each answer, and diagrams kept as Mermaid
 * blocks (which Azure DevOps wikis draw).
 */
export function conversationMarkdown({ title, items, repositories, targets, exportedAt = new Date() }: ExportInput): string {
  const searched = targets
    .map((t) => {
      const repo = repositories.find((r) => r.id === t.repositoryId);
      return repo ? `${repo.name} (${shortBranch(t.branch)})` : null;
    })
    .filter(Boolean)
    .join(", ");

  const out: string[] = [`# ${oneLine(title) || "Conversation"}`, ""];
  out.push(`_Exported from Chat with code on ${exportedAt.toLocaleString()}${searched ? ` · Searched: ${searched}` : ""}_`, "");

  for (const item of items) {
    if (item.role === "user") {
      const heading = oneLine(item.content);
      out.push("---", "", `## ${heading.length > 120 ? `${heading.slice(0, 117).trimEnd()}…` : heading}`, "");
      if (heading.length > 120 || item.content.includes("\n")) out.push(quote(item.content), "");
      continue;
    }

    if (!item.content.trim()) {
      if (item.state === "error") out.push(`_No answer: ${item.error ?? "something went wrong"}_`, "");
      continue;
    }

    out.push(linkCitations(item.content.trim(), item.citations), "");
    if (item.state === "stopped") out.push("_(Stopped before the answer was complete.)_", "");
    if (item.citations.length > 0) {
      out.push(item.answerFound === false ? "**Closest files** (the answer wasn't found in them)" : "**Sources**", "");
      for (const c of item.citations) out.push(sourceLine(c));
      out.push("");
    }
  }

  return out.join("\n").replace(/\n{3,}/g, "\n\n").trimEnd() + "\n";
}

/** A file name for the export, from the conversation title. */
export function exportFileName(title: string): string {
  const slug = title
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "")
    .slice(0, 60);
  return `${slug || "conversation"}.md`;
}

function sourceLine(c: Citation): string {
  const lines = c.startLine === c.endLine ? `line ${c.startLine}` : `lines ${c.startLine}–${c.endLine}`;
  const label = `${c.path}, ${lines}`;
  const where = [c.symbol && `\`${c.symbol}\``, c.repository && `${c.repository} · ${shortBranch(c.branch)}`].filter(Boolean).join(" · ");
  return `${c.number}. ${c.url ? `[${escapeLinkText(label)}](${c.url})` : label}${where ? ` — ${where}` : ""}`;
}

/** Turn [1] markers into links to the cited lines, leaving code blocks and inline code alone. */
function linkCitations(markdown: string, citations: Citation[]): string {
  const urls = new Map(citations.filter((c) => c.url).map((c) => [c.number, c.url!]));
  if (urls.size === 0) return markdown;

  return markdown
    .split(/(```[\s\S]*?```)/g)
    .map((part, i) =>
      i % 2 === 1
        ? part // fenced code block
        : part
            .split(/(`[^`\n]*`)/g)
            .map((piece, j) =>
              j % 2 === 1 ? piece : piece.replace(/\[(\d+)\](?!\()/g, (marker, n: string) => (urls.has(Number(n)) ? `[[${n}]](${urls.get(Number(n))})` : marker)),
            )
            .join(""),
    )
    .join("");
}

const oneLine = (text: string) => text.replace(/\s+/g, " ").trim();
const quote = (text: string) => text.split("\n").map((line) => `> ${line}`).join("\n");
const escapeLinkText = (text: string) => text.replace(/[[\]]/g, "\\$&");
