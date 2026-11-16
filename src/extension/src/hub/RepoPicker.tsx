import { useEffect, useMemo, useRef, useState } from "react";
import type { Branch, ChatTarget, Repository } from "../common/api";
import { Icon } from "../common/Icon";

export const shortBranch = (name: string) => name.replace(/^refs\/heads\//, "");

export function targetKey(t: ChatTarget) {
  return `${t.repositoryId}|${t.branch}`;
}

export function isSearchable(branch: Branch) {
  return branch.status === "Ready" || branch.fileCount > 0;
}

export function branchStatus(branch: Branch): { text: string; tone: "ready" | "busy" | "failed" | "" } {
  switch (branch.status) {
    case "Ready":
      return { text: "Ready", tone: "ready" };
    case "Indexing":
      return { text: branch.progressTotal > 0 ? `Indexing ${branch.progressDone}/${branch.progressTotal}` : "Indexing", tone: "busy" };
    case "Queued":
      return { text: "Queued", tone: "busy" };
    case "Failed":
      return { text: "Indexing failed", tone: "failed" };
    default:
      return { text: "Not indexed", tone: "" };
  }
}

interface Props {
  repositories: Repository[];
  selected: ChatTarget[];
  onChange(targets: ChatTarget[]): void;
  disabled?: boolean;
}

/** Choose which repository branches to search. Branches that aren't indexed yet are shown but can't be picked. */
export function RepoPicker({ repositories, selected, onChange, disabled }: Props) {
  const [open, setOpen] = useState(false);
  const [filter, setFilter] = useState("");
  const root = useRef<HTMLDivElement>(null);
  const search = useRef<HTMLInputElement>(null);
  const selectedKeys = new Set(selected.map(targetKey));

  useEffect(() => {
    if (!open) return;
    search.current?.focus();
    const onDown = (e: MouseEvent) => !root.current?.contains(e.target as Node) && setOpen(false);
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && setOpen(false);
    document.addEventListener("mousedown", onDown);
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("mousedown", onDown);
      document.removeEventListener("keydown", onKey);
    };
  }, [open]);

  const groups = useMemo(() => {
    const term = filter.trim().toLowerCase();
    const byProject = new Map<string, Repository[]>();
    for (const repo of repositories) {
      if (term && !`${repo.project} ${repo.name}`.toLowerCase().includes(term)) continue;
      byProject.set(repo.project, [...(byProject.get(repo.project) ?? []), repo]);
    }
    return [...byProject.entries()];
  }, [repositories, filter]);

  const toggle = (target: ChatTarget) =>
    onChange(selectedKeys.has(targetKey(target)) ? selected.filter((t) => targetKey(t) !== targetKey(target)) : [...selected, target]);

  const labelFor = (t: ChatTarget) => {
    const repo = repositories.find((r) => r.id === t.repositoryId);
    return repo ? `${repo.name} · ${shortBranch(t.branch)}` : shortBranch(t.branch);
  };

  return (
    <div className="targets">
      <span className="label" id="targets-label">
        Searching
      </span>
      {selected.map((t) => (
        <span className="chip" key={targetKey(t)}>
          <Icon name="repo" />
          <span title={labelFor(t)}>{labelFor(t)}</span>
          <button type="button" onClick={() => toggle(t)} aria-label={`Stop searching ${labelFor(t)}`} disabled={disabled}>
            <Icon name="close" />
          </button>
        </span>
      ))}
      <div className="picker" ref={root}>
        <button type="button" className="btn small" onClick={() => setOpen(!open)} aria-expanded={open} aria-haspopup="dialog" disabled={disabled}>
          <Icon name="plus" />
          {selected.length === 0 ? "Choose repositories" : "Add"}
        </button>
        {open && (
          <div className="popover" role="dialog" aria-labelledby="targets-label">
            <input ref={search} type="search" placeholder="Filter repositories" value={filter} onChange={(e) => setFilter(e.target.value)} aria-label="Filter repositories" />
            <div className="popover-list">
              {groups.length === 0 && <div className="history-empty">No repositories match.</div>}
              {groups.map(([project, repos]) => (
                <div key={project} role="group" aria-label={project}>
                  <div className="popover-group">{project}</div>
                  {repos.flatMap((repo) =>
                    repo.branches.map((branch) => {
                      const target = { repositoryId: repo.id, branch: branch.name };
                      const usable = isSearchable(branch);
                      const status = branchStatus(branch);
                      return (
                        <label key={targetKey(target)} className={`option${usable ? "" : " disabled"}`} title={usable ? undefined : "This branch can't be searched until it's indexed."}>
                          <input type="checkbox" checked={selectedKeys.has(targetKey(target))} disabled={!usable} onChange={() => toggle(target)} />
                          <span className="name">
                            {repo.name} <span className="branch">· {shortBranch(branch.name)}</span>
                          </span>
                          <span className={`status ${status.tone}`}>{status.text}</span>
                        </label>
                      );
                    }),
                  )}
                </div>
              ))}
            </div>
            <div className="popover-foot">
              <span>{selected.length === 0 ? "Pick one or more" : `${selected.length} selected`}</span>
              <button type="button" className="btn small primary" onClick={() => setOpen(false)}>
                Done
              </button>
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
