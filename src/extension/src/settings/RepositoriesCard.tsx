import { Fragment, useCallback, useEffect, useState } from "react";
import type { AdminRepository, Api } from "../common/api";
import { Icon } from "../common/Icon";
import { branchStatus, shortBranch } from "../hub/RepoPicker";

/**
 * Which repositories and branches of this project are indexed, with live status.
 * Admins pick branches per repository; everyone else sees the status.
 */
export function RepositoriesCard({ api, projectId, canEdit }: { api: Api; projectId: string; canEdit: boolean }) {
  const [repos, setRepos] = useState<AdminRepository[]>();
  const [error, setError] = useState<string>();
  const [editing, setEditing] = useState<string>();
  const [filter, setFilter] = useState("");
  const [confirming, setConfirming] = useState<string>(); // "<repo id>|<branch>" whose rebuild is being confirmed
  const [sending, setSending] = useState<string>();

  const reindex = async (repositoryId: string, branch: string, full: boolean) => {
    const key = `${repositoryId}|${branch}`;
    setSending(key);
    try {
      await api.admin.reindex(repositoryId, branch, full);
      setConfirming(undefined);
      await load();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setSending(undefined);
    }
  };

  const load = useCallback(() => api.admin.repositories(projectId).then(setRepos).catch((e: Error) => setError(e.message)), [api, projectId]);

  useEffect(() => {
    void load();
  }, [load]);

  // Refresh while anything is queued or indexing, so progress moves without reloading.
  const busy = repos?.some((r) => r.branches.some((b) => b.status === "Queued" || b.status === "Indexing")) ?? false;
  useEffect(() => {
    if (!busy) return;
    const timer = setInterval(() => void load(), 5000);
    return () => clearInterval(timer);
  }, [busy, load]);

  const replace = (repo: AdminRepository) => setRepos((list) => list?.map((r) => (r.id === repo.id ? repo : r)));
  const visible = (repos ?? []).filter((r) => r.name.toLowerCase().includes(filter.trim().toLowerCase()));
  const indexedCount = repos?.filter((r) => r.indexed).length ?? 0;

  return (
    <section className="card" aria-labelledby="repos-heading">
      <h2 id="repos-heading">Repositories</h2>
      <p>
        Choose which branches people can ask about. New branches are indexed in the background; it usually takes a few minutes.
        {repos && ` ${indexedCount} of ${repos.length} repositories indexed.`}
      </p>

      {error && (
        <div className="notice error" role="alert">
          <Icon name="warn" />
          <div className="grow">{error}</div>
        </div>
      )}
      {!repos && !error && <p role="status">Loading repositories…</p>}
      {repos && repos.length === 0 && <p className="muted">This project has no repositories you can read.</p>}
      {repos && repos.length > 6 && (
        <input type="search" className="filter" placeholder="Filter repositories" aria-label="Filter repositories" value={filter} onChange={(e) => setFilter(e.target.value)} />
      )}

      <ul className="repo-list">
        {visible.map((repo) => (
          <li key={repo.id} className="repo">
            <div className="repo-head">
              <Icon name="repo" />
              <strong className="grow">{repo.name}</strong>
              {!repo.indexed && <span className="muted small">Not indexed</span>}
              {canEdit && editing !== repo.id && (
                <button type="button" className="btn small" onClick={() => setEditing(repo.id)}>
                  {repo.indexed ? "Change branches" : "Index"}
                </button>
              )}
            </div>

            {editing === repo.id ? (
              <BranchEditor api={api} repo={repo} onDone={(updated) => { if (updated) replace(updated); setEditing(undefined); }} />
            ) : (
              repo.branches.length > 0 && (
                <table className="branches">
                  <thead>
                    <tr>
                      <th scope="col">Branch</th>
                      <th scope="col">Status</th>
                      <th scope="col">Files</th>
                      <th scope="col">Last indexed</th>
                      {canEdit && <th scope="col"><span className="sr-only">Actions</span></th>}
                    </tr>
                  </thead>
                  <tbody>
                    {repo.branches.map((b) => {
                      const status = branchStatus({ ...b, fileCount: b.fileCount });
                      const key = `${repo.id}|${b.name}`;
                      const busyRow = sending === key || b.status === "Queued" || b.status === "Indexing";
                      return (
                        <Fragment key={b.name}>
                        <tr>
                          <td>
                            {shortBranch(b.name)} {b.isDefault && <span className="muted small">default</span>}
                          </td>
                          <td>
                            <span className={`status ${status.tone}`}>{status.text}</span>
                            {b.lastError && <div className="error-text">{b.lastError}</div>}
                          </td>
                          <td>{b.fileCount.toLocaleString()}</td>
                          <td className="muted" title={b.lastIndexedAt ? new Date(b.lastIndexedAt).toLocaleString() : undefined}>
                            {b.lastIndexedAt ? timeAgo(b.lastIndexedAt) : "Never"}
                          </td>
                          {canEdit && (
                            <td className="row-actions">
                              <button
                                type="button"
                                className="btn ghost small"
                                disabled={busyRow}
                                onClick={() => void reindex(repo.id, b.name, b.status === "Failed")}
                                title={b.status === "Failed" ? "Index again from scratch" : "Index what changed since the last run"}
                              >
                                <Icon name="refresh" /> Re-index
                              </button>
                              <button
                                type="button"
                                className="btn ghost small"
                                disabled={busyRow}
                                aria-expanded={confirming === key}
                                onClick={() => setConfirming(confirming === key ? undefined : key)}
                                title="Delete this branch's index and index every file again, e.g. after changing the embedding model or if answers look wrong"
                              >
                                Rebuild
                              </button>
                            </td>
                          )}
                        </tr>
                        {canEdit && confirming === key && (
                          <tr className="confirm-row">
                            <td colSpan={5}>
                              <div className="notice warn" role="group" aria-label={`Rebuild ${shortBranch(b.name)} from scratch`}>
                                <Icon name="warn" />
                                <div className="grow">
                                  <strong>Rebuild {shortBranch(b.name)} from scratch?</strong> Every file is indexed again, which costs the full indexing fee for this branch.
                                  Usually Re-index is enough; rebuild after changing the embedding model or if answers look wrong.
                                </div>
                                <button type="button" className="btn small danger" onClick={() => void reindex(repo.id, b.name, true)} disabled={sending === key}>
                                  {sending === key ? "Starting…" : "Rebuild"}
                                </button>
                                <button type="button" className="btn ghost small" onClick={() => setConfirming(undefined)} disabled={sending === key}>
                                  Cancel
                                </button>
                              </div>
                            </td>
                          </tr>
                        )}
                        </Fragment>
                      );
                    })}
                  </tbody>
                </table>
              )
            )}
          </li>
        ))}
      </ul>
    </section>
  );
}

/** "just now", "10 min ago", "3 h ago", "2 days ago", then the date. */
export function timeAgo(iso: string, now = Date.now()): string {
  const minutes = Math.round((now - new Date(iso).getTime()) / 60_000);
  if (minutes < 1) return "just now";
  if (minutes < 60) return `${minutes} min ago`;
  const hours = Math.round(minutes / 60);
  if (hours < 24) return `${hours} h ago`;
  const days = Math.round(hours / 24);
  if (days < 7) return `${days} day${days === 1 ? "" : "s"} ago`;
  return new Date(iso).toLocaleDateString();
}

function BranchEditor({ api, repo, onDone }: { api: Api; repo: AdminRepository; onDone(updated?: AdminRepository): void }) {
  const [branches, setBranches] = useState<string[]>();
  const [selected, setSelected] = useState<Set<string>>(
    () => new Set(repo.branches.length > 0 ? repo.branches.map((b) => b.name) : repo.defaultBranch ? [repo.defaultBranch] : []),
  );
  const [error, setError] = useState<string>();
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    api.admin.branches(repo.id).then(setBranches).catch((e: Error) => setError(e.message));
  }, [api, repo.id]);

  const toggle = (name: string) => {
    const next = new Set(selected);
    if (next.has(name)) next.delete(name);
    else next.add(name);
    setSelected(next);
  };

  const save = async () => {
    setSaving(true);
    setError(undefined);
    try {
      onDone(await api.admin.setBranches(repo.id, [...selected]));
    } catch (e) {
      setError((e as Error).message);
      setSaving(false);
    }
  };

  const removing = repo.branches.filter((b) => !selected.has(b.name));
  return (
    <div className="branch-editor">
      {!branches && !error && <p role="status">Loading branches…</p>}
      {branches && (
        <div className="branch-options" role="group" aria-label={`Branches of ${repo.name} to index`}>
          {[...branches]
            .sort((a, b) => Number(b === repo.defaultBranch) - Number(a === repo.defaultBranch) || a.localeCompare(b))
            .map((name) => (
              <label key={name} className="option">
                <input type="checkbox" checked={selected.has(name)} onChange={() => toggle(name)} />
                <span className="name">
                  {shortBranch(name)} {name === repo.defaultBranch && <span className="branch">· default</span>}
                </span>
              </label>
            ))}
        </div>
      )}
      {removing.length > 0 && (
        <div className="notice warn">
          <Icon name="warn" />
          <div className="grow">
            {selected.size === 0 ? `${repo.name} will be removed from the index.` : `The index of ${removing.map((b) => shortBranch(b.name)).join(", ")} will be deleted.`} People won't be able to ask about it.
          </div>
        </div>
      )}
      {error && (
        <div className="notice error" role="alert">
          <Icon name="warn" />
          <div className="grow">{error}</div>
        </div>
      )}
      <div className="actions">
        <button type="button" className="btn primary" onClick={() => void save()} disabled={saving || !branches}>
          {saving ? "Saving…" : selected.size === 0 && repo.indexed ? "Remove from index" : "Save"}
        </button>
        <button type="button" className="btn ghost" onClick={() => onDone()} disabled={saving}>
          Cancel
        </button>
      </div>
    </div>
  );
}
