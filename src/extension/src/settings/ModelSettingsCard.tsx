import { useEffect, useState } from "react";
import { ApiError, type Api, type CheckResult, type ModelSettings, type SaveSettings, type SettingsResponse } from "../common/api";
import { Icon } from "../common/Icon";

type AuthChoice = "key" | "identity" | "inherit";

interface Form {
  endpoint: string;
  chat: string;
  embedding: string;
  dimensions: string;
  auth: AuthChoice;
  apiKey: string;
  removeKey: boolean;
  vectorStore: string;
  searchEndpoint: string;
  searchIndex: string;
  searchKey: string;
  removeSearchKey: boolean;
}

function toForm(s: ModelSettings | null, isProject: boolean): Form {
  return {
    endpoint: s?.azureOpenAIEndpoint ?? "",
    chat: s?.chatDeployment ?? "",
    embedding: s?.embeddingDeployment ?? "",
    dimensions: s?.embeddingDimensions?.toString() ?? "",
    auth: s?.useManagedIdentity == null ? (isProject ? "inherit" : "key") : s.useManagedIdentity ? "identity" : "key",
    apiKey: "",
    removeKey: false,
    vectorStore: s?.vectorStore ?? (isProject ? "" : "PgVector"),
    searchEndpoint: s?.azureSearchEndpoint ?? "",
    searchIndex: s?.azureSearchIndexName ?? "",
    searchKey: "",
    removeSearchKey: false,
  };
}

function toRequest(f: Form): SaveSettings {
  const text = (v: string) => (v.trim() === "" ? null : v.trim());
  return {
    azureOpenAIEndpoint: text(f.endpoint),
    chatDeployment: text(f.chat),
    embeddingDeployment: text(f.embedding),
    embeddingDimensions: f.dimensions.trim() === "" ? null : Number(f.dimensions),
    useManagedIdentity: f.auth === "inherit" ? null : f.auth === "identity",
    apiKey: f.removeKey ? "" : f.apiKey.trim() === "" ? null : f.apiKey.trim(),
    vectorStore: text(f.vectorStore),
    azureSearchEndpoint: text(f.searchEndpoint),
    azureSearchIndexName: text(f.searchIndex),
    azureSearchApiKey: f.removeSearchKey ? "" : f.searchKey.trim() === "" ? null : f.searchKey.trim(),
  };
}

/**
 * Azure OpenAI and vector store settings. On the organization page these are the defaults;
 * on a project page, empty fields use the organization value (shown as the placeholder).
 */
export function ModelSettingsCard({ api, projectId, projectName }: { api: Api; projectId?: string; projectName?: string }) {
  const isProject = projectId !== undefined;
  const [data, setData] = useState<SettingsResponse>();
  const [form, setForm] = useState<Form>();
  const [loadError, setLoadError] = useState<string>();
  const [saving, setSaving] = useState(false);
  const [message, setMessage] = useState<{ tone: "ok" | "error"; text: string }>();
  const [testing, setTesting] = useState(false);
  const [test, setTest] = useState<{ embedding: CheckResult; chat: CheckResult }>();

  useEffect(() => {
    api.admin
      .settings(projectId)
      .then((d) => {
        setData(d);
        setForm(toForm(isProject ? d.project : d.organization, isProject));
      })
      .catch((e: Error) => setLoadError(e.message));
  }, [api, projectId, isProject]);

  if (loadError) {
    return (
      <section className="card">
        <h2>Azure OpenAI</h2>
        <div className="notice error" role="alert">
          <Icon name="warn" />
          <div className="grow">{loadError}</div>
        </div>
      </section>
    );
  }
  if (!data || !form) {
    return (
      <section className="card" role="status">
        <h2>Azure OpenAI</h2>
        <p>Loading settings…</p>
      </section>
    );
  }

  const stored = isProject ? data.project! : data.organization;
  const org = data.organization;
  const readOnly = !data.canEdit;
  const set = <K extends keyof Form>(key: K, value: Form[K]) => {
    setForm({ ...form, [key]: value });
    setMessage(undefined);
  };
  const inherited = (value: string | number | null | undefined, fallback: string) => (isProject ? (value ? `Organization: ${value}` : "Organization default not set") : fallback);
  const dirty = JSON.stringify(toRequest(form)) !== JSON.stringify(toRequest(toForm(stored, isProject)));
  const embeddingChanged =
    form.embedding.trim() !== (stored.embeddingDeployment ?? "") ||
    form.dimensions.trim() !== (stored.embeddingDimensions?.toString() ?? "") ||
    form.vectorStore !== (stored.vectorStore ?? (isProject ? "" : "PgVector"));

  const save = async () => {
    setSaving(true);
    setMessage(undefined);
    try {
      const result = await api.admin.saveSettings(toRequest(form), projectId);
      setData(result.settings);
      setForm(toForm(isProject ? result.settings.project : result.settings.organization, isProject));
      setMessage({
        tone: "ok",
        text: result.reindexQueued > 0 ? `Saved. ${result.reindexQueued} branch(es) will be re-indexed with the new embedding model.` : "Saved.",
      });
    } catch (e) {
      setMessage({ tone: "error", text: e instanceof ApiError ? e.message : String(e) });
    } finally {
      setSaving(false);
    }
  };

  const runTest = async () => {
    setTesting(true);
    setTest(undefined);
    try {
      setTest(await api.admin.testSettings(projectId));
    } catch (e) {
      setMessage({ tone: "error", text: (e as Error).message });
    } finally {
      setTesting(false);
    }
  };

  const removeOverride = async () => {
    if (!projectId) return;
    setSaving(true);
    try {
      await api.admin.removeProjectSettings(projectId);
      const fresh = await api.admin.settings(projectId);
      setData(fresh);
      setForm(toForm(fresh.project, true));
      setMessage({ tone: "ok", text: "This project now uses the organization settings." });
    } catch (e) {
      setMessage({ tone: "error", text: (e as Error).message });
    } finally {
      setSaving(false);
    }
  };

  const keyState = stored.apiKey.isSet ? (readOnly ? `A key is saved (${stored.apiKey.hint}).` : `Saved (${stored.apiKey.hint}). Leave empty to keep it.`) : isProject ? (org.apiKey.isSet ? "Uses the organization key." : "No key saved.") : "No key saved yet.";

  return (
    <section className="card" aria-labelledby="models-heading">
      <h2 id="models-heading">{isProject ? `Azure OpenAI and index for ${projectName ?? "this project"}` : "Azure OpenAI and index"}</h2>
      <p>{isProject ? "Leave a field empty to use the organization setting." : "Defaults for every project. Projects can override them in Project settings."}</p>

      {readOnly && (
        <div className="notice warn" style={{ marginBottom: 14 }}>
          <Icon name="warn" />
          <div className="grow">Only Chat with code admins can change these settings. Admins are listed in the service's CHAT_ADMINS setting.</div>
        </div>
      )}
      {data.problem && !isProject && (
        <div className="notice error" style={{ marginBottom: 14 }} role="status">
          <Icon name="warn" />
          <div className="grow">{data.problem}</div>
        </div>
      )}

      <fieldset className="form" disabled={readOnly || saving}>
        <Field label="Endpoint" hint="Your Azure OpenAI resource, e.g. https://your-resource.openai.azure.com/">
          {(id) => <input id={id} type="url" value={form.endpoint} onChange={(e) => set("endpoint", e.target.value)} placeholder={inherited(org.azureOpenAIEndpoint, "https://your-resource.openai.azure.com/")} />}
        </Field>

        <div className="field">
          <span className="field-label" id="auth-label">
            Sign-in
          </span>
          <div className="segmented" role="radiogroup" aria-labelledby="auth-label">
            {isProject && <Radio name="auth" value="inherit" current={form.auth} onChange={(v) => set("auth", v)} label="Organization setting" />}
            <Radio name="auth" value="key" current={form.auth} onChange={(v) => set("auth", v)} label="API key" />
            <Radio name="auth" value="identity" current={form.auth} onChange={(v) => set("auth", v)} label="Managed identity" />
          </div>
          {form.auth === "identity" && <span className="field-hint">The service's managed identity needs the Cognitive Services OpenAI User role on the resource.</span>}
        </div>

        {form.auth === "inherit" && <span className="field-hint">Uses the organization's sign-in setting{org.apiKey.isSet ? " and key" : ""}.</span>}
        {form.auth === "key" && (
          <Field label="API key" hint={keyState}>
            {(id) => (
              <div className="row">
                <input id={id} type="password" autoComplete="off" value={form.apiKey} onChange={(e) => set("apiKey", e.target.value)} placeholder={stored.apiKey.isSet ? "••••••••••••" : "Paste the key"} disabled={form.removeKey} />
                {stored.apiKey.isSet && (
                  <label className="check">
                    <input type="checkbox" checked={form.removeKey} onChange={(e) => set("removeKey", e.target.checked)} /> Remove
                  </label>
                )}
              </div>
            )}
          </Field>
        )}

        <div className="grid2">
          <Field label="Chat deployment" hint="Answers questions, e.g. gpt-6-luna.">
            {(id) => <input id={id} value={form.chat} onChange={(e) => set("chat", e.target.value)} placeholder={inherited(org.chatDeployment, "gpt-6-luna")} />}
          </Field>
          <Field label="Embedding deployment" hint="Indexes code, e.g. text-embedding-3-small.">
            {(id) => <input id={id} value={form.embedding} onChange={(e) => set("embedding", e.target.value)} placeholder={inherited(org.embeddingDeployment, "text-embedding-3-small")} />}
          </Field>
        </div>

        <details className="advanced">
          <summary>Advanced</summary>
          <div className="grid2">
            <Field label="Embedding dimensions" hint="PostgreSQL needs 1536; Azure AI Search accepts 256–3072.">
              {(id) => <input id={id} type="number" min={256} max={3072} value={form.dimensions} onChange={(e) => set("dimensions", e.target.value)} placeholder={inherited(org.embeddingDimensions, "1536")} />}
            </Field>
            <Field label="Vector store" hint="Where the index is kept.">
              {(id) => (
                <select id={id} value={form.vectorStore} onChange={(e) => set("vectorStore", e.target.value)}>
                  {isProject && <option value="">Organization setting</option>}
                  <option value="PgVector">PostgreSQL (pgvector)</option>
                  <option value="AzureAISearch">Azure AI Search</option>
                </select>
              )}
            </Field>
          </div>
        </details>

        {(form.vectorStore === "AzureAISearch" || (form.vectorStore === "" && org.vectorStore === "AzureAISearch")) && (
          <div className="subsection">
            <h3>Azure AI Search</h3>
            <div className="grid2">
              <Field label="Search endpoint" hint="e.g. https://your-search.search.windows.net">
                {(id) => <input id={id} type="url" value={form.searchEndpoint} onChange={(e) => set("searchEndpoint", e.target.value)} placeholder={inherited(org.azureSearchEndpoint, "https://your-search.search.windows.net")} />}
              </Field>
              <Field label="Index name" hint="Created automatically on the first indexing run.">
                {(id) => <input id={id} value={form.searchIndex} onChange={(e) => set("searchIndex", e.target.value)} placeholder={inherited(org.azureSearchIndexName, "chat-with-code")} />}
              </Field>
            </div>
            {form.auth === "identity" || (form.auth === "inherit" && org.useManagedIdentity) ? (
              <span className="field-hint">Uses the managed identity, which needs the Search Index Data Contributor and Search Service Contributor roles.</span>
            ) : (
              <Field label="Search admin key" hint={stored.azureSearchApiKey.isSet ? (readOnly ? `A key is saved (${stored.azureSearchApiKey.hint}).` : `Saved (${stored.azureSearchApiKey.hint}). Leave empty to keep it.`) : isProject && org.azureSearchApiKey.isSet ? "Uses the organization key." : "No key saved."}>
                {(id) => (
                  <div className="row">
                    <input id={id} type="password" autoComplete="off" value={form.searchKey} onChange={(e) => set("searchKey", e.target.value)} placeholder={stored.azureSearchApiKey.isSet ? "••••••••••••" : "Paste the admin key"} disabled={form.removeSearchKey} />
                    {stored.azureSearchApiKey.isSet && (
                      <label className="check">
                        <input type="checkbox" checked={form.removeSearchKey} onChange={(e) => set("removeSearchKey", e.target.checked)} /> Remove
                      </label>
                    )}
                  </div>
                )}
              </Field>
            )}
          </div>
        )}

        {embeddingChanged && (stored.embeddingDeployment || isProject) && (
          <div className="notice warn">
            <Icon name="warn" />
            <div className="grow">Changing the embedding model or the vector store re-indexes every affected branch from scratch. Answers may be incomplete until that finishes.</div>
          </div>
        )}
      </fieldset>

      <div className="actions">
        {!readOnly && (
          <button type="button" className="btn primary" onClick={() => void save()} disabled={saving}>
            {saving ? "Saving…" : "Save"}
          </button>
        )}
        {!readOnly && (
          <button type="button" className="btn" onClick={() => void runTest()} disabled={testing || saving || dirty} title={dirty ? "Save your changes first; the test uses the saved settings." : undefined}>
            <Icon name="refresh" /> {testing ? "Testing…" : "Test saved settings"}
          </button>
        )}
        {!readOnly && isProject && (
          <button type="button" className="btn ghost" onClick={() => void removeOverride()} disabled={saving}>
            Use organization settings
          </button>
        )}
        {dirty && !readOnly && <span className="muted small">Unsaved changes</span>}
        {!dirty && stored.updatedAt && (
          <span className="muted small">
            Last changed {new Date(stored.updatedAt).toLocaleString()}
            {stored.updatedBy ? ` by ${stored.updatedBy}` : ""}
          </span>
        )}
      </div>

      {message && (
        <p className={message.tone === "ok" ? "saved" : "notice error"} role={message.tone === "ok" ? "status" : "alert"}>
          {message.text}
        </p>
      )}
      {test && (
        <ul className="checks" aria-live="polite">
          {(["embedding", "chat"] as const).map((k) => (
            <li key={k}>
              <span className={`dot ${test[k].ok ? "ok" : "bad"}`} aria-hidden="true" />
              <div>
                <div>{k === "embedding" ? "Embedding deployment" : "Chat deployment"}</div>
                <div className="muted">{test[k].message}</div>
              </div>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

let fieldCounter = 0;

function Field({ label, hint, children }: { label: string; hint?: string; children: (id: string) => React.ReactNode }) {
  const [id] = useState(() => `field-${++fieldCounter}`);
  return (
    <div className="field">
      <label className="field-label" htmlFor={id}>
        {label}
      </label>
      {children(id)}
      {hint && <span className="field-hint">{hint}</span>}
    </div>
  );
}

function Radio<T extends string>({ name, value, current, onChange, label }: { name: string; value: T; current: string; onChange(v: T): void; label: string }) {
  return (
    <label className={`segment${current === value ? " selected" : ""}`}>
      <input type="radio" name={name} value={value} checked={current === value} onChange={() => onChange(value)} />
      {label}
    </label>
  );
}
