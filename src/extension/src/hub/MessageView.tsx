import { useEffect, useMemo, useRef, useState } from "react";
import type { Citation } from "../common/api";
import { Icon } from "../common/Icon";
import { renderAnswer, renderMermaid } from "../common/markdown";
import { shortBranch } from "./RepoPicker";

export interface ChatItem {
  key: string;
  role: "user" | "assistant";
  content: string;
  citations: Citation[];
  answerFound?: boolean;
  state: "streaming" | "done" | "error" | "stopped";
  error?: string;
}

const prefersDark = () => document.documentElement.classList.contains("theme-dark");

export function MessageView({ item, onRetry }: { item: ChatItem; onRetry?(): void }) {
  if (item.role === "user") {
    return <div className="msg-user">{item.content}</div>;
  }
  return <AssistantMessage item={item} onRetry={onRetry} />;
}

function AssistantMessage({ item, onRetry }: { item: ChatItem; onRetry?(): void }) {
  const body = useRef<HTMLDivElement>(null);
  const [copied, setCopied] = useState(false);
  const citations = useMemo(() => new Map(item.citations.map((c) => [c.number, c])), [item.citations]);
  const html = useMemo(() => renderAnswer(item.content, citations), [item.content, citations]);

  // Diagrams are drawn once the answer is complete, so half-written Mermaid isn't parsed.
  useEffect(() => {
    if (item.state !== "streaming" && body.current) void renderMermaid(body.current, prefersDark());
  }, [html, item.state]);

  const copy = async () => {
    await navigator.clipboard.writeText(item.content).catch(() => undefined);
    setCopied(true);
    setTimeout(() => setCopied(false), 1500);
  };

  const waiting = item.state === "streaming" && item.content.length === 0;
  return (
    <div className="msg-assistant">
      <div className="avatar" aria-hidden="true">
        <Icon name="chat" />
      </div>
      <div className="msg-body">
        {waiting ? (
          <div className="thinking" role="status">
            <span className="cursor" /> Searching the code…
          </div>
        ) : (
          <div className="answer" ref={body} aria-live={item.state === "streaming" ? "polite" : undefined}>
            <div dangerouslySetInnerHTML={{ __html: html }} />
            {item.state === "streaming" && <span className="cursor" aria-hidden="true" />}
          </div>
        )}

        {item.state === "stopped" && <p className="muted">Stopped.</p>}
        {item.state === "error" && (
          <div className="notice error" role="alert" style={{ marginTop: 8 }}>
            <Icon name="warn" />
            <div className="grow">{item.error}</div>
            {onRetry && (
              <button type="button" className="btn small" onClick={onRetry}>
                <Icon name="refresh" /> Try again
              </button>
            )}
          </div>
        )}

        {item.citations.length > 0 && <Sources citations={item.citations} found={item.answerFound !== false} />}

        {item.state === "done" && item.content && (
          <div className="msg-actions">
            <button type="button" className="btn ghost small" onClick={copy} aria-label="Copy answer">
              <Icon name={copied ? "check" : "copy"} /> {copied ? "Copied" : "Copy"}
            </button>
          </div>
        )}
      </div>
    </div>
  );
}

function Sources({ citations, found }: { citations: Citation[]; found: boolean }) {
  return (
    <div className="sources">
      <div className="sources-head">
        <Icon name="file" />
        {found ? "Sources" : "Closest files (the answer wasn't found in them)"}
      </div>
      <ol>
        {citations.map((c) => {
          const lines = c.startLine === c.endLine ? `line ${c.startLine}` : `lines ${c.startLine}–${c.endLine}`;
          const meta = [lines, c.symbol, c.repository && `${c.repository} · ${shortBranch(c.branch)}`].filter(Boolean).join("  ·  ");
          const content = (
            <>
              <span className="cite n">{c.number}</span>
              <span className="path" title={c.path}>
                {c.path}
              </span>
              <span className="meta">{meta}</span>
            </>
          );
          return (
            <li key={`${c.number}-${c.path}`}>
              {c.url ? (
                <a className="source" href={c.url} target="_blank" rel="noopener noreferrer" title="Open in Azure DevOps">
                  {content}
                </a>
              ) : (
                <div className="source">{content}</div>
              )}
            </li>
          );
        })}
      </ol>
    </div>
  );
}
