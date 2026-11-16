import { useEffect, useState } from "react";
import type { AdminRepository, Api, PushSetup } from "../common/api";
import { Icon } from "../common/Icon";
import { timeAgo } from "./RepositoriesCard";

/**
 * How the index learns about new commits: checking on a schedule (always available) and
 * Azure DevOps push notifications (service hooks), with setup steps and whether they arrive.
 */
export function UpdatesCard({ api, projectId, canEdit }: { api: Api; projectId: string; canEdit: boolean }) {
  const [setup, setSetup] = useState<PushSetup>();
  const [repos, setRepos] = useState<AdminRepository[]>([]);
  const [error, setError] = useState<string>();

  useEffect(() => {
    Promise.all([api.admin.pushSetup(), api.admin.repositories(projectId)])
      .then(([s, r]) => {
        setSetup(s);
        setRepos(r.filter((x) => x.indexed));
      })
      .catch((e: Error) => setError(e.message));
  }, [api, projectId]);

  const polling = !setup
    ? ""
    : !setup.pollingEnabled
      ? "Checking for new commits is turned off on the service."
      : setup.pollIntervalMinutes === 0
        ? "The service checks every indexed branch for new commits once, when it starts. Set INDEX_POLL_MINUTES (for example to 5) on the service to keep checking."
        : `The service checks every indexed branch for new commits every ${setup.pollIntervalMinutes} minute${setup.pollIntervalMinutes === 1 ? "" : "s"}.`;

  return (
    <section className="card" aria-labelledby="updates-heading">
      <h2 id="updates-heading">Keeping the index up to date</h2>
      <p>New commits are indexed automatically. Push notifications make it nearly instant; checking on a schedule works even when Azure DevOps can't reach the service.</p>
      {error && (
        <div className="notice error" role="alert">
          <Icon name="warn" />
          <div className="grow">{error}</div>
        </div>
      )}
      {setup && (
        <>
          <h3 className="card-sub">Checking for new commits</h3>
          <p className="muted">{polling}</p>

          <h3 className="card-sub">Push notifications</h3>
          {!setup.webhookConfigured ? (
            <p className="muted">Not set up on the service. Whoever runs it can set WEBHOOK_USERNAME and WEBHOOK_PASSWORD to turn them on.</p>
          ) : canEdit ? (
            <ol className="steps">
              <li>
                Open <strong>Project settings &gt; Service hooks</strong> and select <strong>Create subscription</strong> (+).
              </li>
              <li>
                Choose <strong>Web Hooks</strong>, then <strong>Next</strong>.
              </li>
              <li>
                Trigger: <strong>Code pushed</strong>. Repository and branch: <strong>[Any]</strong>. Then <strong>Next</strong>.
              </li>
              <li>
                Fill in:
                <dl className="kv copyable">
                  <dt>URL</dt>
                  <dd>
                    <Copy value={`${api.baseUrl}${setup.webhookPath}`} />
                  </dd>
                  <dt>Basic authentication username</dt>
                  <dd>
                    <Copy value={setup.webhookUsername ?? ""} />
                  </dd>
                  <dt>Basic authentication password</dt>
                  <dd className="muted">The service's WEBHOOK_PASSWORD (ask whoever runs it; it's never shown here)</dd>
                  <dt>Resource details to send</dt>
                  <dd>
                    <strong>All</strong>
                  </dd>
                  <dt>Messages / Detailed messages</dt>
                  <dd>None</dd>
                </dl>
              </li>
              <li>
                Select <strong>Test</strong> (it should succeed), then <strong>Finish</strong>.
              </li>
            </ol>
          ) : (
            <p className="muted">Set up by a Chat with code admin in Project settings &gt; Service hooks.</p>
          )}

          {setup.webhookConfigured && repos.length > 0 && (
            <ul className="push-status">
              {repos.map((r) => (
                <li key={r.id}>
                  <span className={`dot ${r.lastPushNotificationAt ? "ok" : ""}`} aria-hidden="true" />
                  <span className="grow">{r.name}</span>
                  <span className="muted small">
                    {r.lastPushNotificationAt ? `Last push notification ${timeAgo(r.lastPushNotificationAt)}` : "No push notification received yet"}
                  </span>
                </li>
              ))}
            </ul>
          )}
        </>
      )}
    </section>
  );
}

function Copy({ value }: { value: string }) {
  const [copied, setCopied] = useState(false);
  return (
    <span className="copy-row">
      <code className="mono">{value}</code>
      <button
        type="button"
        className="btn ghost small"
        aria-label={`Copy ${value}`}
        onClick={async () => {
          await navigator.clipboard.writeText(value).catch(() => undefined);
          setCopied(true);
          setTimeout(() => setCopied(false), 1500);
        }}
      >
        <Icon name={copied ? "check" : "copy"} /> {copied ? "Copied" : "Copy"}
      </button>
    </span>
  );
}
