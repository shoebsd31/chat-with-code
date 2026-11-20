import { useCallback, useEffect, useId, useMemo, useState } from "react";
import type { Api, ModelPrice, UsageReport, UserUsage } from "../common/api";
import { Icon } from "../common/Icon";

const currencies = ["USD", "EUR", "GBP", "INR", "AUD", "CAD", "CHF", "JPY", "SEK", "NOK", "DKK", "PLN", "BRL", "ZAR", "SGD", "AED"];

/** Money in the configured currency; a non-ISO label is shown as "12.30 XYZ". */
export function formatMoney(amount: number, currency: string, digits = 2): string {
  try {
    return new Intl.NumberFormat(undefined, { style: "currency", currency, minimumFractionDigits: digits, maximumFractionDigits: digits }).format(amount);
  } catch {
    return `${amount.toFixed(digits)} ${currency}`;
  }
}

export const formatTokens = (n: number) =>
  n >= 1_000_000 ? `${(n / 1_000_000).toFixed(n >= 10_000_000 ? 0 : 1)}M` : n >= 1_000 ? `${Math.round(n / 1_000)}k` : String(n);

/** The last 12 months, newest first, as "2026-11". */
function recentMonths(now = new Date()): string[] {
  return Array.from({ length: 12 }, (_, i) => new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth() - i, 1)).toISOString().slice(0, 7));
}

const monthLabel = (month: string) => new Date(`${month}-01T00:00:00Z`).toLocaleDateString(undefined, { month: "long", year: "numeric", timeZone: "UTC" });

/**
 * Organization settings, admins only: what Chat with code costs, model prices, the monthly allowance
 * per user (with a daily cap), and individual allowances.
 */
export function UsageCard({ api }: { api: Api }) {
  const [month, setMonth] = useState(() => recentMonths()[0]);
  const [report, setReport] = useState<UsageReport>();
  const [error, setError] = useState<string>();

  const load = useCallback(
    () => api.admin.usage(month).then((r) => (setReport(r), setError(undefined)), (e: Error) => setError(e.message)),
    [api, month],
  );
  useEffect(() => {
    void load();
  }, [load]);

  return (
    <section className="card" aria-labelledby="usage-heading">
      <h2 id="usage-heading">Usage and limits</h2>
      <p>Every answer is recorded. Enter your model prices to see what Chat with code costs, and set a monthly allowance per user to cap spending. Admins aren't limited.</p>

      {error && (
        <div className="notice error" role="alert">
          <Icon name="warn" />
          <div className="grow">{error}</div>
        </div>
      )}
      {!report && !error && <p role="status">Loading usage…</p>}
      {report && (
        <>
          <Summary report={report} month={month} onMonth={setMonth} />
          <LimitsForm api={api} report={report} onSaved={setReport} />
          <People api={api} report={report} onSaved={setReport} isCurrentMonth={month === recentMonths()[0]} />
        </>
      )}
    </section>
  );
}

function Summary({ report, month, onMonth }: { report: UsageReport; month: string; onMonth(month: string): void }) {
  const id = useId();
  const people = report.users.filter((u) => u.questions > 0).length;
  return (
    <div className="subsection">
      <div className="row usage-head">
        <h3 className="grow">Spending</h3>
        <label htmlFor={id} className="sr-only">
          Month
        </label>
        <select id={id} className="filter compact" value={month} onChange={(e) => onMonth(e.target.value)}>
          {recentMonths().map((m) => (
            <option key={m} value={m}>
              {monthLabel(m)}
            </option>
          ))}
        </select>
      </div>
      <div className="stats">
        <Stat label="Estimated cost" value={formatMoney(report.total.cost, report.currency)} />
        <Stat label="Questions" value={report.total.questions.toLocaleString()} />
        <Stat label="People" value={people.toLocaleString()} />
        <Stat label="Tokens in / out" value={`${formatTokens(report.total.inputTokens)} / ${formatTokens(report.total.outputTokens)}`} />
      </div>
      {report.unpricedModels.length > 0 && (
        <div className="notice warn">
          <Icon name="warn" />
          <div className="grow">
            No price for {report.unpricedModels.join(", ")}, so {report.unpricedModels.length === 1 ? "its" : "their"} usage isn't in the cost. Add prices below.
          </div>
        </div>
      )}
      {report.organization.inputTokens + report.organization.outputTokens > 0 && (
        <p className="field-hint">Includes {formatMoney(report.organization.cost, report.currency)} for writing starter questions, which doesn't count against anyone's allowance.</p>
      )}
    </div>
  );
}

function Stat({ label, value }: { label: string; value: string }) {
  return (
    <div className="stat">
      <div className="stat-value">{value}</div>
      <div className="stat-label">{label}</div>
    </div>
  );
}

type PriceRow = { model: string; input: string; output: string };

function LimitsForm({ api, report, onSaved }: { api: Api; report: UsageReport; onSaved(report: UsageReport): void }) {
  const [currency, setCurrency] = useState(report.currency);
  const [allowance, setAllowance] = useState(report.monthlyAllowance?.toString() ?? "");
  const [prices, setPrices] = useState<PriceRow[]>(() =>
    report.models.map((model) => {
      const p = report.prices.find((x) => x.model.toLowerCase() === model.toLowerCase());
      return { model, input: p?.inputPerMillion.toString() ?? "", output: p?.outputPerMillion.toString() ?? "" };
    }),
  );
  const [saving, setSaving] = useState(false);
  const [status, setStatus] = useState<{ tone: "ok" | "error"; text: string }>();
  const allowanceId = useId();
  const currencyId = useId();

  const amount = allowance.trim() === "" ? null : Number(allowance);
  const invalid = amount !== null && (!Number.isFinite(amount) || amount < 0);
  const dailyCap = amount && !invalid ? (amount * report.dailyCapPercent) / 100 : null;

  const save = async () => {
    const parsed: ModelPrice[] = [];
    for (const row of prices) {
      if (row.input.trim() === "" && row.output.trim() === "") continue;
      const input = Number(row.input || "0");
      const output = Number(row.output || "0");
      if (![input, output].every((n) => Number.isFinite(n) && n >= 0)) {
        setStatus({ tone: "error", text: `Check the prices for ${row.model}.` });
        return;
      }
      parsed.push({ model: row.model, inputPerMillion: input, outputPerMillion: output });
    }
    setSaving(true);
    setStatus(undefined);
    try {
      onSaved(await api.admin.saveUsageSettings({ currency: currency.trim() || "USD", monthlyAllowance: amount, prices: parsed }));
      setStatus({ tone: "ok", text: "Saved" });
    } catch (e) {
      setStatus({ tone: "error", text: (e as Error).message });
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="subsection">
      <h3>Limits</h3>
      <fieldset className="form" disabled={saving}>
        <div className="grid2">
          <div className="field">
            <label className="field-label" htmlFor={allowanceId}>
              Monthly allowance per user
            </label>
            <div className="row">
              <input id={allowanceId} type="number" min="0" step="0.5" inputMode="decimal" value={allowance} onChange={(e) => setAllowance(e.target.value)} placeholder="No limit" aria-invalid={invalid} />
              <span className="muted">{currency}</span>
            </div>
            <span className="field-hint">
              {invalid
                ? "Enter an amount of 0 or more, or leave it empty for no limit."
                : dailyCap !== null
                  ? `Daily limit: ${report.dailyCapPercent}% of this (${formatMoney(dailyCap, currency)}). Resets on the 1st, UTC.`
                  : "Empty: no limit. Usage is still recorded."}
            </span>
          </div>
          <div className="field">
            <label className="field-label" htmlFor={currencyId}>
              Currency
            </label>
            <select id={currencyId} value={currency} onChange={(e) => setCurrency(e.target.value)}>
              {[...new Set([currency, ...currencies])].map((c) => (
                <option key={c}>{c}</option>
              ))}
            </select>
            <span className="field-hint">A label for prices and allowances; nothing is converted.</span>
          </div>
        </div>

        <div className="field">
          <span className="field-label" id="prices-label">
            Model prices, per million tokens
          </span>
          {prices.length === 0 ? (
            <span className="field-hint">Chat models appear here once one is set in the model settings.</span>
          ) : (
            <table className="prices" aria-labelledby="prices-label">
              <thead>
                <tr>
                  <th scope="col">Chat model</th>
                  <th scope="col">Input</th>
                  <th scope="col">Output</th>
                </tr>
              </thead>
              <tbody>
                {prices.map((row, i) => (
                  <tr key={row.model}>
                    <td className="mono">{row.model}</td>
                    {(["input", "output"] as const).map((side) => (
                      <td key={side}>
                        <input
                          type="number"
                          min="0"
                          step="0.01"
                          inputMode="decimal"
                          aria-label={`${row.model} ${side} price per million tokens`}
                          placeholder="No price"
                          value={row[side]}
                          onChange={(e) => setPrices(prices.map((p, j) => (j === i ? { ...p, [side]: e.target.value } : p)))}
                        />
                      </td>
                    ))}
                  </tr>
                ))}
              </tbody>
            </table>
          )}
          <span className="field-hint">From your Azure OpenAI pricing (Azure pricing page or your invoice). Prices apply to this month's usage too.</span>
        </div>
      </fieldset>
      <div className="actions">
        <button type="button" className="btn primary" onClick={() => void save()} disabled={saving || invalid}>
          {saving ? "Saving…" : "Save limits"}
        </button>
        {status && (
          <span className={status.tone === "ok" ? "muted" : "error-text"} role="status">
            {status.tone === "ok" && <Icon name="check" />} {status.text}
          </span>
        )}
      </div>
    </div>
  );
}

function People({ api, report, onSaved, isCurrentMonth }: { api: Api; report: UsageReport; onSaved(report: UsageReport): void; isCurrentMonth: boolean }) {
  const [editing, setEditing] = useState<string>();
  const sorted = useMemo(() => report.users, [report.users]);
  if (sorted.length === 0) {
    return (
      <div className="subsection">
        <h3>People</h3>
        <p className="muted small">Nobody has asked anything this month yet.</p>
      </div>
    );
  }

  return (
    <div className="subsection">
      <h3>People</h3>
      <table className="people">
        <thead>
          <tr>
            <th scope="col">Name</th>
            <th scope="col">Questions</th>
            <th scope="col">Cost</th>
            <th scope="col">Allowance used</th>
            <th scope="col">Monthly allowance</th>
          </tr>
        </thead>
        <tbody>
          {sorted.map((u) => (
            <tr key={u.userId}>
              <td>{u.userName ?? u.userId}</td>
              <td>{u.questions.toLocaleString()}</td>
              <td>{formatMoney(u.cost, report.currency)}</td>
              <td>{u.allowance ? <Meter percent={u.percent} /> : <span className="muted small">No limit</span>}</td>
              <td>
                {editing === u.userId ? (
                  <AllowanceEditor api={api} user={u} currency={report.currency} onDone={(r) => (r && onSaved(r), setEditing(undefined))} />
                ) : (
                  <div className="row allowance">
                    <span className="grow">
                      {u.ownAllowance !== null ? formatMoney(u.ownAllowance, report.currency) : <span className="muted small">{report.monthlyAllowance !== null ? `Default (${formatMoney(report.monthlyAllowance, report.currency)})` : "Default"}</span>}
                    </span>
                    {isCurrentMonth && (
                      <button type="button" className="btn ghost small" onClick={() => setEditing(u.userId)} aria-label={`Change ${u.userName ?? u.userId}'s allowance`}>
                        Change
                      </button>
                    )}
                  </div>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function Meter({ percent }: { percent: number }) {
  const tone = percent >= 100 ? "over" : percent >= 80 ? "high" : "";
  return (
    <div className="meter-wrap" title={`${percent}% of this month's allowance`}>
      <div className={`meter ${tone}`} role="meter" aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.min(percent, 100)} aria-label="Allowance used">
        <div style={{ width: `${Math.min(percent, 100)}%` }} />
      </div>
      <span className="small">{percent}%</span>
    </div>
  );
}

function AllowanceEditor({ api, user, currency, onDone }: { api: Api; user: UserUsage; currency: string; onDone(report?: UsageReport): void }) {
  const [value, setValue] = useState(user.ownAllowance?.toString() ?? "");
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string>();
  const amount = Number(value);
  const invalid = value.trim() === "" || !Number.isFinite(amount) || amount < 0;

  const save = async (next: number | null) => {
    setSaving(true);
    setError(undefined);
    try {
      onDone(await api.admin.setUserAllowance(user.userId, next, user.userName));
    } catch (e) {
      setError((e as Error).message);
      setSaving(false);
    }
  };

  return (
    <div className="allowance-editor">
      <div className="row">
        <input
          type="number"
          min="0"
          step="0.5"
          inputMode="decimal"
          autoFocus
          value={value}
          onChange={(e) => setValue(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter" && !invalid) void save(amount);
            if (e.key === "Escape") onDone();
          }}
          aria-label={`Monthly allowance for ${user.userName ?? user.userId} in ${currency}`}
          disabled={saving}
        />
        <button type="button" className="btn primary small" onClick={() => void save(amount)} disabled={saving || invalid} aria-label={`Save ${user.userName ?? user.userId}'s allowance`}>
          Save
        </button>
        {user.ownAllowance !== null && (
          <button type="button" className="btn ghost small" onClick={() => void save(null)} disabled={saving} title="Use the organization's allowance again">
            Use default
          </button>
        )}
        <button type="button" className="btn ghost small" onClick={() => onDone()} disabled={saving}>
          Cancel
        </button>
      </div>
      {error && <div className="error-text">{error}</div>}
    </div>
  );
}
