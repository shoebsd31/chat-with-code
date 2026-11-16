import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { ApiError, type Api, type ChatTarget, type ConversationSummary, type Repository } from "../common/api";
import type { Host } from "../common/host";
import { Icon } from "../common/Icon";
import { MessageView, type ChatItem } from "./MessageView";
import { isSearchable, RepoPicker, targetKey } from "./RepoPicker";

const examples = [
  "How is authentication implemented?",
  "Where are orders saved to the database?",
  "Draw a diagram of how a request flows through the API.",
  "What does the README say about running locally?",
];

type LoadState = { kind: "loading" } | { kind: "ready" } | { kind: "error"; error: ApiError };

const selectionKey = (org: string) => `cwc:targets:${org}`;

export function App({ host, api }: { host: Host; api: Api }) {
  const [load, setLoad] = useState<LoadState>({ kind: "loading" });
  const [repositories, setRepositories] = useState<Repository[]>([]);
  const [conversations, setConversations] = useState<ConversationSummary[]>([]);
  const [targets, setTargets] = useState<ChatTarget[]>([]);
  const [conversationId, setConversationId] = useState<string>();
  const [items, setItems] = useState<ChatItem[]>([]);
  const [input, setInput] = useState("");
  const [busy, setBusy] = useState(false);
  const [sidebarOpen, setSidebarOpen] = useState(() => window.innerWidth > 760);
  const abort = useRef<AbortController | null>(null);
  const scroller = useRef<HTMLDivElement>(null);
  const textarea = useRef<HTMLTextAreaElement>(null);
  const stickToBottom = useRef(true);

  // ---- Loading ----
  const loadAll = useCallback(async () => {
    setLoad({ kind: "loading" });
    try {
      const [repos, convs] = await Promise.all([api.repositories(), api.conversations()]);
      setRepositories(repos);
      setConversations(convs);
      // Restore the last selection, keeping only branches that can still be searched.
      const searchable = new Set(repos.flatMap((r) => r.branches.filter(isSearchable).map((b) => targetKey({ repositoryId: r.id, branch: b.name }))));
      let saved: ChatTarget[] = [];
      try {
        saved = JSON.parse(localStorage.getItem(selectionKey(host.context.organization)) ?? "[]") as ChatTarget[];
      } catch {
        // Ignore unreadable saved state.
      }
      const restored = saved.filter((t) => searchable.has(targetKey(t)));
      if (restored.length > 0) setTargets(restored);
      else {
        const first = repos.find((r) => r.branches.some(isSearchable));
        const branch = first?.branches.find((b) => b.isDefault && isSearchable(b)) ?? first?.branches.find(isSearchable);
        setTargets(first && branch ? [{ repositoryId: first.id, branch: branch.name }] : []);
      }
      setLoad({ kind: "ready" });
    } catch (error) {
      setLoad({ kind: "error", error: error instanceof ApiError ? error : new ApiError(String(error), 0) });
    } finally {
      host.loaded();
    }
  }, [api, host]);

  useEffect(() => {
    void loadAll();
  }, [loadAll]);

  useEffect(() => {
    try {
      localStorage.setItem(selectionKey(host.context.organization), JSON.stringify(targets));
    } catch {
      // Storage may be unavailable; selection just won't be remembered.
    }
  }, [targets, host.context.organization]);

  // Keep the newest text in view while streaming, unless the user scrolled up to read.
  useEffect(() => {
    const el = scroller.current;
    if (el && stickToBottom.current) el.scrollTop = el.scrollHeight;
  }, [items]);

  const onScroll = () => {
    const el = scroller.current;
    if (el) stickToBottom.current = el.scrollHeight - el.scrollTop - el.clientHeight < 80;
  };

  // ---- Conversations ----
  const newChat = () => {
    abort.current?.abort();
    setConversationId(undefined);
    setItems([]);
    setInput("");
    textarea.current?.focus();
  };

  const openConversation = async (id: string) => {
    if (busy) return;
    if (window.innerWidth <= 760) setSidebarOpen(false);
    try {
      const conversation = await api.conversation(id);
      setConversationId(id);
      if (conversation.targets.length > 0) setTargets(conversation.targets);
      setItems(
        conversation.messages.map((m) => ({
          key: m.id,
          role: m.role === "User" ? "user" : "assistant",
          content: m.content,
          citations: m.citations,
          answerFound: m.citations.length === 0 || m.citations.some((c) => c.used),
          state: "done",
        })),
      );
      stickToBottom.current = true;
    } catch (error) {
      setItems([{ key: crypto.randomUUID(), role: "assistant", content: "", citations: [], state: "error", error: (error as Error).message }]);
    }
  };

  const deleteConversation = async (id: string) => {
    try {
      await api.deleteConversation(id);
      setConversations((list) => list.filter((c) => c.id !== id));
      if (id === conversationId) newChat();
    } catch {
      // The list refreshes on the next load; nothing else to do.
    }
  };

  // ---- Asking ----
  const ask = async (question: string) => {
    const text = question.trim();
    if (!text || busy) return;
    if (targets.length === 0) {
      setItems((list) => [...list, { key: crypto.randomUUID(), role: "assistant", content: "", citations: [], state: "error", error: "Choose at least one repository to search first." }]);
      return;
    }

    const answerKey = crypto.randomUUID();
    setItems((list) => [
      ...list,
      { key: crypto.randomUUID(), role: "user", content: text, citations: [], state: "done" },
      { key: answerKey, role: "assistant", content: "", citations: [], state: "streaming" },
    ]);
    setInput("");
    setBusy(true);
    stickToBottom.current = true;
    const controller = new AbortController();
    abort.current = controller;
    const update = (change: Partial<ChatItem> | ((item: ChatItem) => Partial<ChatItem>)) =>
      setItems((list) => list.map((item) => (item.key === answerKey ? { ...item, ...(typeof change === "function" ? change(item) : change) } : item)));

    let finished = false;
    try {
      await api.ask(
        { question: text, conversationId, targets },
        (event) => {
          switch (event.type) {
            case "conversation":
              setConversationId(event.conversationId);
              setConversations((list) => [
                { id: event.conversationId, title: event.title, updatedAt: new Date().toISOString(), targets },
                ...list.filter((c) => c.id !== event.conversationId),
              ]);
              break;
            case "delta":
              update((item) => ({ content: item.content + event.text }));
              break;
            case "citations":
              update({ citations: event.citations, answerFound: event.answerFound });
              break;
            case "done":
              finished = true;
              update({ state: "done" });
              break;
            case "error":
              finished = true;
              update({ state: "error", error: event.message });
              break;
          }
        },
        controller.signal,
      );
      if (!finished) update({ state: "error", error: "The answer was cut off. Try again." });
    } catch (error) {
      if ((error as Error).name === "AbortError") update({ state: "stopped" });
      else update({ state: "error", error: (error as Error).message });
    } finally {
      setBusy(false);
      abort.current = null;
      textarea.current?.focus();
    }
  };

  const retry = (index: number) => {
    const question = [...items.slice(0, index)].reverse().find((i) => i.role === "user");
    if (!question) return;
    setItems((list) => list.slice(0, index - 1));
    void ask(question.content);
  };

  const onKeyDown = (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === "Enter" && !e.shiftKey && !e.nativeEvent.isComposing) {
      e.preventDefault();
      void ask(input);
    }
  };

  // Grow the text box with its content.
  useEffect(() => {
    const el = textarea.current;
    if (!el) return;
    el.style.height = "auto";
    el.style.height = `${Math.min(el.scrollHeight, 200)}px`;
  }, [input]);

  const groupedHistory = useMemo(() => groupByDay(conversations), [conversations]);
  const hasSearchable = repositories.some((r) => r.branches.some(isSearchable));

  if (load.kind === "loading") {
    return (
      <div className="center-state" role="status">
        <span className="cursor" /> Loading…
      </div>
    );
  }

  if (load.kind === "error") {
    return (
      <div className="center-state">
        <Icon name="warn" />
        <h1>{load.error.title}</h1>
        <p>{load.error.message}</p>
        <button type="button" className="btn primary" onClick={() => void loadAll()}>
          <Icon name="refresh" /> Try again
        </button>
      </div>
    );
  }

  return (
    <div className={`app${sidebarOpen ? " sidebar-open" : " sidebar-hidden"}`}>
      <aside className="sidebar" aria-label="Conversations" hidden={!sidebarOpen}>
        <div className="sidebar-head">
          <button type="button" className="btn" onClick={newChat}>
            <Icon name="plus" /> New chat
          </button>
        </div>
        <nav className="history">
          {conversations.length === 0 && <div className="history-empty">Your conversations appear here. They're kept for 30 days after the last message.</div>}
          {groupedHistory.map(([label, list]) => (
            <div key={label}>
              <h3>{label}</h3>
              {list.map((c) => (
                <div key={c.id} className={`history-item${c.id === conversationId ? " active" : ""}`}>
                  <button type="button" onClick={() => void openConversation(c.id)} title={c.title} aria-current={c.id === conversationId ? "true" : undefined}>
                    {c.title}
                  </button>
                  <button type="button" className="btn ghost small delete" onClick={() => void deleteConversation(c.id)} aria-label={`Delete "${c.title}"`}>
                    <Icon name="trash" />
                  </button>
                </div>
              ))}
            </div>
          ))}
        </nav>
      </aside>

      <main className="main">
        <div className="topbar">
          <button type="button" className="btn ghost small" onClick={() => setSidebarOpen(!sidebarOpen)} aria-label={sidebarOpen ? "Hide conversations" : "Show conversations"} aria-expanded={sidebarOpen}>
            <Icon name="menu" />
          </button>
          <RepoPicker repositories={repositories} selected={targets} onChange={setTargets} disabled={busy} />
        </div>

        <div className="scroll" ref={scroller} onScroll={onScroll}>
          {items.length === 0 ? (
            <div className="empty">
              <div className="avatar" aria-hidden="true">
                <Icon name="chat" />
              </div>
              <h1>Ask about your code</h1>
              {hasSearchable ? (
                <>
                  <p className="muted">Answers come only from the repositories you pick above, with links to the exact lines.</p>
                  <div className="examples">
                    {examples.map((q) => (
                      <button type="button" key={q} className="example" onClick={() => void ask(q)}>
                        {q}
                      </button>
                    ))}
                  </div>
                </>
              ) : (
                <div className="notice warn" style={{ marginTop: 16, textAlign: "left" }}>
                  <Icon name="warn" />
                  <div className="grow">
                    <strong>No repositories you can read are indexed yet.</strong>
                    Ask an admin to add one in Project settings &gt; Chat with code. Indexing usually takes a few minutes.
                  </div>
                </div>
              )}
            </div>
          ) : (
            <div className="thread">
              {items.map((item, index) => (
                <MessageView key={item.key} item={item} onRetry={item.state === "error" && index > 0 ? () => retry(index) : undefined} />
              ))}
            </div>
          )}
        </div>

        <div className="composer-wrap">
          <form
            className="composer"
            onSubmit={(e) => {
              e.preventDefault();
              void ask(input);
            }}
          >
            <label htmlFor="question" className="sr-only">
              Ask a question about the selected repositories
            </label>
            <textarea
              id="question"
              ref={textarea}
              rows={1}
              value={input}
              onChange={(e) => setInput(e.target.value)}
              onKeyDown={onKeyDown}
              placeholder={targets.length === 0 ? "Choose a repository above, then ask a question" : "Ask a question about the code…"}
              disabled={!hasSearchable}
              maxLength={4000}
              autoFocus
            />
            {busy ? (
              <button type="button" className="btn" onClick={() => abort.current?.abort()} aria-label="Stop answering">
                <Icon name="stop" /> Stop
              </button>
            ) : (
              <button type="submit" className="btn primary" disabled={!input.trim() || targets.length === 0} aria-label="Send">
                <Icon name="send" />
              </button>
            )}
          </form>
          <p className="composer-hint">Enter to send, Shift+Enter for a new line. Answers can be wrong; check the linked sources.</p>
        </div>
      </main>
    </div>
  );
}

function groupByDay(conversations: ConversationSummary[]): [string, ConversationSummary[]][] {
  const startOfToday = new Date();
  startOfToday.setHours(0, 0, 0, 0);
  const day = 86_400_000;
  const groups = new Map<string, ConversationSummary[]>();
  for (const c of conversations) {
    const t = new Date(c.updatedAt).getTime();
    const label = t >= startOfToday.getTime() ? "Today" : t >= startOfToday.getTime() - day ? "Yesterday" : t >= startOfToday.getTime() - 7 * day ? "Previous 7 days" : "Older";
    groups.set(label, [...(groups.get(label) ?? []), c]);
  }
  return [...groups.entries()];
}
