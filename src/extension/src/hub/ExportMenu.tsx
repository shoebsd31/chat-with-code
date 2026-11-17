import { useEffect, useRef, useState } from "react";
import { copyText } from "../common/clipboard";
import { downloadText } from "../common/download";
import { Icon } from "../common/Icon";
import { conversationMarkdown, exportFileName, type ExportInput } from "./exportMarkdown";

type Status = { tone: "ok" | "error"; text: string } | null;

/** "Export" in the top bar: copy the conversation as Markdown, or download it as a .md file. */
export function ExportMenu({ input, disabled }: { input: ExportInput; disabled: boolean }) {
  const [open, setOpen] = useState(false);
  const [status, setStatus] = useState<Status>(null);
  const root = useRef<HTMLDivElement>(null);
  const firstItem = useRef<HTMLButtonElement>(null);

  // Close on Escape or a click outside; focus the first item when opened.
  useEffect(() => {
    if (!open) return;
    firstItem.current?.focus();
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && setOpen(false);
    const onClick = (e: MouseEvent) => !root.current?.contains(e.target as Node) && setOpen(false);
    document.addEventListener("keydown", onKey);
    document.addEventListener("mousedown", onClick);
    return () => {
      document.removeEventListener("keydown", onKey);
      document.removeEventListener("mousedown", onClick);
    };
  }, [open]);

  useEffect(() => {
    if (!status) return;
    const timer = setTimeout(() => setStatus(null), status.tone === "ok" ? 2000 : 5000);
    return () => clearTimeout(timer);
  }, [status]);

  const copy = async () => {
    setOpen(false);
    const ok = await copyText(conversationMarkdown(input));
    setStatus(ok ? { tone: "ok", text: "Copied as Markdown" } : { tone: "error", text: "Couldn't copy. Try Download instead." });
  };

  const download = () => {
    setOpen(false);
    const ok = downloadText(exportFileName(input.title), conversationMarkdown(input));
    setStatus(ok ? { tone: "ok", text: "Downloaded" } : { tone: "error", text: "Your browser blocked the download. Try Copy as Markdown instead." });
  };

  return (
    <div className="export-menu" ref={root}>
      <button
        type="button"
        className="btn ghost small"
        aria-haspopup="menu"
        aria-expanded={open}
        disabled={disabled}
        onClick={() => setOpen(!open)}
        title={disabled ? "Ask a question first" : "Export this conversation"}
      >
        <Icon name="download" /> Export
      </button>
      {open && (
        <div className="menu" role="menu" aria-label="Export conversation">
          <button type="button" role="menuitem" ref={firstItem} onClick={() => void copy()}>
            <Icon name="copy" /> Copy as Markdown
          </button>
          <button type="button" role="menuitem" onClick={download}>
            <Icon name="download" /> Download .md file
          </button>
        </div>
      )}
      <span className={`export-status ${status?.tone ?? ""}`} role="status">
        {status?.text}
      </span>
    </div>
  );
}
