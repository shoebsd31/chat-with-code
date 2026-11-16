import { useCallback, useEffect, useState } from "react";
import type { Api, Me, ServiceInfo } from "../common/api";
import type { Host } from "../common/host";
import { Icon } from "../common/Icon";

type Check = { state: "pending" | "ok" | "bad"; label: string; detail?: string };

/**
 * Organization and project settings. Phase 4a shows the connection: the backend address built
 * into this extension and a step-by-step check of the connection. Model and repository settings
 * are added in phase 4b.
 */
export function Settings({ host, api, scope }: { host: Host; api: Api; scope: "organization" | "project" }) {
  const [checks, setChecks] = useState<Check[]>([]);
  const [info, setInfo] = useState<ServiceInfo>();
  const [running, setRunning] = useState(false);

  const test = useCallback(async () => {
    setRunning(true);
    const results: Check[] = [
      { state: "pending", label: "Reach the Chat with code service" },
      { state: "pending", label: "Service is set up for this organization" },
      { state: "pending", label: "Service accepts this extension (extension secret)" },
      { state: "pending", label: "Azure DevOps confirms your sign-in" },
    ];
    const show = () => setChecks([...results]);
    const fail = (index: number, detail: string) => {
      results[index] = { ...results[index], state: "bad", detail };
      for (let i = index + 1; i < results.length; i++) results[i] = { ...results[i], state: "pending", detail: "Not checked" };
      show();
    };
    show();

    let serviceInfo: ServiceInfo;
    try {
      serviceInfo = await api.info();
      setInfo(serviceInfo);
      results[0] = { ...results[0], state: "ok", detail: `${serviceInfo.name} ${serviceInfo.version}` };
    } catch (error) {
      fail(0, (error as Error).message);
      setRunning(false);
      return;
    }

    if (serviceInfo.organization.toLowerCase() !== host.context.organization.toLowerCase()) {
      fail(1, `The service is set up for "${serviceInfo.organization}", but this is "${host.context.organization}". Set ADO_ORGANIZATION on the service.`);
      setRunning(false);
      return;
    }
    results[1] = { ...results[1], state: "ok", detail: serviceInfo.organization };

    if (!serviceInfo.extensionConnected) {
      fail(2, "The service has no extension secret. Set ADO_EXTENSION_SECRET to this extension's secret (Marketplace publisher portal > extension > Certificate).");
      setRunning(false);
      return;
    }

    try {
      const me: Me = await api.me();
      results[2] = { ...results[2], state: "ok" };
      results[3] = { ...results[3], state: "ok", detail: `Signed in as ${me.displayName}` };
      show();
    } catch (error) {
      const message = (error as Error).message;
      if (message.includes("extension")) fail(2, message);
      else {
        results[2] = { ...results[2], state: "ok" };
        fail(3, message);
      }
    }
    setRunning(false);
  }, [api, host]);

  useEffect(() => {
    void test().finally(() => host.loaded());
  }, [test, host]);

  const allOk = checks.length > 0 && checks.every((c) => c.state === "ok");
  return (
    <div className="settings">
      <h1>Chat with code</h1>
      <p>{scope === "organization" ? "Connection and settings for the whole organization." : `Settings for the ${host.context.projectName ?? "current"} project.`}</p>

      <section className="card" aria-labelledby="connection-heading">
        <h2 id="connection-heading">Connection</h2>
        <p>The service address is built into this extension when it's packaged, so it can't be changed from Azure DevOps.</p>
        <dl className="kv">
          <dt>Service address</dt>
          <dd className="mono">{api.baseUrl || "Not set (package the extension with --backend-url)"}</dd>
          <dt>Organization</dt>
          <dd>{host.context.organization}</dd>
          {info && (
            <>
              <dt>Service version</dt>
              <dd>{info.version}</dd>
            </>
          )}
        </dl>
        <button type="button" className="btn" onClick={() => void test()} disabled={running}>
          <Icon name="refresh" /> {running ? "Testing…" : "Test connection"}
        </button>
        <ul className="checks" aria-live="polite">
          {checks.map((c) => (
            <li key={c.label}>
              <span className={`dot ${c.state}`} aria-hidden="true" />
              <div>
                <div>
                  {c.label}
                  <span className="sr-only">: {c.state === "ok" ? "passed" : c.state === "bad" ? "failed" : "pending"}</span>
                </div>
                {c.detail && <div className="muted">{c.detail}</div>}
              </div>
            </li>
          ))}
        </ul>
        {allOk && (
          <p className="muted" style={{ marginTop: 12, marginBottom: 0 }}>
            Everything works. People can use Chat with code under Repos.
          </p>
        )}
      </section>

      <section className="card">
        <h2>Models and repositories</h2>
        <p style={{ marginBottom: 0 }}>
          {scope === "organization"
            ? "Azure OpenAI and vector store settings for the organization will be managed here."
            : "Choosing which repositories and branches to index, and project-specific model settings, will be managed here."}{" "}
          Until then, they're set in the service's <span className="mono">.env</span> file and with the <span className="mono">index</span> command (see the README).
        </p>
      </section>
    </div>
  );
}
