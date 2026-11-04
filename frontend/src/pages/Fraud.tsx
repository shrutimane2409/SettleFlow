import { useEffect, useState } from 'react';
import { FlaskConical, Plus, Save, Trash2 } from 'lucide-react';
import { api } from '../api/client';
import { can, useSession } from '../auth';
import { Card, Empty, Stat } from '../components/ui';
import { timeAgo, usePolling } from '../hooks';
import type { FraudRules, Simulation } from '../types';

type RuleDef = { key: keyof FraudRules; label: string; hint: string; fields: { key: keyof FraudRules; label: string; step?: string }[] };

const RULES: RuleDef[] = [
  {
    key: 'highAmountEnabled',
    label: 'High amount',
    hint: 'Amount above a threshold',
    fields: [
      { key: 'highAmountThreshold', label: 'Above' },
      { key: 'highAmountScore', label: 'Score' },
    ],
  },
  {
    key: 'countryRiskEnabled',
    label: 'Country risk',
    hint: 'Outside low-risk countries',
    fields: [
      { key: 'lowRiskCountries', label: 'Low-risk countries' },
      { key: 'countryRiskScore', label: 'Score' },
    ],
  },
  {
    key: 'roundAmountEnabled',
    label: 'Round amount',
    hint: 'Amount is a multiple of N',
    fields: [
      { key: 'roundAmountMultiple', label: 'Multiple of' },
      { key: 'roundAmountScore', label: 'Score' },
    ],
  },
  {
    key: 'velocityEnabled',
    label: 'Velocity',
    hint: 'Many payments from one customer',
    fields: [
      { key: 'velocityLimit', label: 'Payments ≥' },
      { key: 'velocityWindow', label: 'Within (hh:mm:ss)' },
      { key: 'velocityScore', label: 'Score' },
    ],
  },
  {
    key: 'modelEnabled',
    label: 'Risk model',
    hint: 'Pluggable ML model (heuristic-logistic-v1)',
    fields: [
      { key: 'modelThreshold', label: 'Probability ≥', step: '0.05' },
      { key: 'modelScore', label: 'Score' },
    ],
  },
  { key: 'blocklistEnabled', label: 'Blocklist', hint: 'Blocklisted customers/countries are declined', fields: [] },
];

export function Fraud({ onSelect }: { onSelect: (id: string) => void }) {
  const me = useSession();
  const { data: active, refresh } = usePolling(api.fraudRules, 15000);
  const { data: perf } = usePolling(() => api.fraudPerformance(30), 15000);
  const [draft, setDraft] = useState<FraudRules | null>(null);
  const [comment, setComment] = useState('');
  const [sim, setSim] = useState<Simulation | null>(null);
  const [msg, setMsg] = useState<{ tone: 'ok' | 'error'; text: string } | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (active && !draft) setDraft(active.rules);
  }, [active, draft]);

  const set = (k: keyof FraudRules, v: string | boolean) => {
    if (!draft) return;
    const current = draft[k];
    setDraft({ ...draft, [k]: typeof current === 'number' ? Number(v) : v });
    setSim(null);
  };

  const run = async (fn: () => Promise<void>) => {
    setBusy(true);
    setMsg(null);
    try {
      await fn();
    } catch (e) {
      setMsg({ tone: 'error', text: e instanceof Error ? e.message : String(e) });
    } finally {
      setBusy(false);
    }
  };

  const changed = active && draft && JSON.stringify(active.rules) !== JSON.stringify(draft);

  return (
    <>
      <section className="stats">
        <Stat
          title="Active rules"
          value={active ? `v${active.version}` : '—'}
          hint={active?.history[0] ? `by ${active.history[0].createdBy}` : 'defaults'}
        />
        <Stat title="Payments (30d)" value={perf?.payments ?? '—'} hint={`${perf?.approved ?? 0} approved`} />
        <Stat
          title="Chargebacks"
          value={perf?.chargebacks ?? '—'}
          tone={perf?.chargebacks ? 'bad' : 'good'}
          hint="disputed after approval"
        />
        <Stat
          title="Missed fraud"
          value={perf ? `${perf.chargebackRateOnApproved}%` : '—'}
          tone={perf && perf.chargebackRateOnApproved > 0.5 ? 'bad' : 'good'}
          hint="of approved charged back"
        />
      </section>

      <section className="grid finance-grid">
        <Card title="Rules" className="wide" aside={changed ? 'unsaved changes' : active ? `v${active.version} active` : ''}>
          {!draft ? (
            <Empty>Loading…</Empty>
          ) : (
            <div className="rules">
              {RULES.map(rule => (
                <div key={rule.key} className={`rule ${draft[rule.key] ? '' : 'off'}`}>
                  <label className="toggle rule-head">
                    <input
                      type="checkbox"
                      checked={Boolean(draft[rule.key])}
                      onChange={e => set(rule.key, e.target.checked)}
                      disabled={!can.review(me)}
                    />
                    <b>{rule.label}</b> <span className="muted-text small">{rule.hint}</span>
                  </label>
                  {rule.fields.length > 0 && (
                    <div className="rule-fields">
                      {rule.fields.map(f => (
                        <label key={f.key}>
                          {f.label}
                          <input
                            value={String(draft[f.key])}
                            step={f.step}
                            disabled={!can.review(me)}
                            onChange={e => set(f.key, e.target.value)}
                          />
                        </label>
                      ))}
                    </div>
                  )}
                </div>
              ))}
              <div className="rule-fields thresholds">
                <label>
                  Review at score ≥
                  <input value={draft.reviewThreshold} disabled={!can.review(me)} onChange={e => set('reviewThreshold', e.target.value)} />
                </label>
                <label>
                  Decline at score ≥
                  <input
                    value={draft.declineThreshold}
                    disabled={!can.review(me)}
                    onChange={e => set('declineThreshold', e.target.value)}
                  />
                </label>
              </div>

              {can.review(me) && (
                <div className="form-actions">
                  <button disabled={busy} onClick={() => run(async () => setSim(await api.simulateFraud(draft, 7)))}>
                    <FlaskConical size={14} /> Simulate on last 7 days
                  </button>
                  {can.admin(me) && (
                    <>
                      <input
                        className="grow"
                        placeholder="Why this change? (audited)"
                        value={comment}
                        onChange={e => setComment(e.target.value)}
                      />
                      <button
                        className="primary"
                        disabled={busy || !changed}
                        onClick={() =>
                          run(async () => {
                            const r = await api.saveFraudRules(draft, comment || undefined);
                            setMsg({ tone: 'ok', text: `Rule set v${r.version} is live (workers pick it up within ~5s)` });
                            setComment('');
                            setDraft(null);
                            setSim(null);
                            await refresh();
                          })
                        }
                      >
                        <Save size={14} /> Activate
                      </button>
                    </>
                  )}
                </div>
              )}
              {msg && <p className={`form-msg ${msg.tone}`}>{msg.text}</p>}

              {sim && (
                <div className="sim">
                  <h3 className="section-h">
                    Simulation: {sim.payments} payments, <b>{sim.changed}</b> decisions change
                  </h3>
                  <div className="sim-grid">
                    {['APPROVED', 'REVIEW', 'DECLINED'].map(d => (
                      <div key={d} className="sim-cell">
                        <span className="muted-text small">{d}</span>
                        <b>
                          {sim.current[d] ?? 0} → {sim.candidate[d] ?? 0}
                        </b>
                      </div>
                    ))}
                    <div className="sim-cell">
                      <span className="muted-text small">Chargebacks caught</span>
                      <b>
                        {sim.chargebacksCaughtCurrent} → {sim.chargebacksCaughtCandidate} of {sim.chargebacksTotal}
                      </b>
                    </div>
                  </div>
                  {Object.entries(sim.transitions).map(([k, v]) => (
                    <span key={k} className="chip">
                      {k}: {v}
                    </span>
                  ))}
                  {sim.examples.length > 0 && (
                    <div className="mini-list">
                      {sim.examples.slice(0, 8).map(x => (
                        <button key={x.paymentId} className="mini-row row" onClick={() => onSelect(x.paymentId)}>
                          <span className="mono small">{x.paymentId.slice(0, 8)}…</span>
                          <span>
                            {x.currency} {x.amount.toFixed(2)} {x.country}
                          </span>
                          <span className="small">
                            {x.from} → <b>{x.to}</b>
                          </span>
                          <span className="muted-text small ellipsis">{x.reasons.join(', ')}</span>
                        </button>
                      ))}
                    </div>
                  )}
                </div>
              )}
            </div>
          )}
        </Card>

        <div className="stack">
          <Card title="Rule performance" aside="last 30 days">
            {!perf || perf.byRule.length === 0 ? (
              <Empty>No scored payments yet.</Empty>
            ) : (
              <div className="table perf-table">
                <div className="tr th">
                  <span>Rule</span>
                  <span>Fired</span>
                  <span>Declined</span>
                  <span>Review</span>
                  <span>Chargebacks</span>
                </div>
                {perf.byRule.map(r => (
                  <div className="tr" key={r.rule}>
                    <span className="mono">{r.rule}</span>
                    <span>{r.fired}</span>
                    <span>{r.declined}</span>
                    <span>{r.inReview}</span>
                    <span className={r.approvedThenChargedBack ? 'bad-text' : 'muted-text'}>{r.approvedThenChargedBack}</span>
                  </div>
                ))}
              </div>
            )}
          </Card>
          <Blocklist />
          <Card title="Versions">
            <div className="mini-list">
              {(active?.history ?? []).map(h => (
                <div key={h.version} className="mini-row">
                  <span className="mono">v{h.version}</span>
                  {h.active ? <em className="badge settled">active</em> : <span />}
                  <span className="muted-text small ellipsis">{h.comment ?? ''}</span>
                  <span className="muted-text small">
                    {h.createdBy} · {timeAgo(h.createdAt)}
                  </span>
                </div>
              ))}
              {(active?.history ?? []).length === 0 && (
                <Empty>Using built-in defaults (v0). Activate a rule set to start versioning.</Empty>
              )}
            </div>
          </Card>
        </div>
      </section>
    </>
  );
}

function Blocklist() {
  const me = useSession();
  const { data, refresh } = usePolling(api.blocklist, 15000);
  const [form, setForm] = useState({ type: 'customer', value: '', reason: '' });
  const [error, setError] = useState<string | null>(null);

  const add = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);
    try {
      await api.addBlocklist(form.type, form.value, form.reason);
      setForm({ ...form, value: '', reason: '' });
      await refresh();
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  };

  return (
    <Card title="Blocklist" aside={`${data?.length ?? 0} entries`}>
      {can.review(me) && (
        <form className="inline-form" onSubmit={add}>
          <label>
            Type
            <select value={form.type} onChange={e => setForm({ ...form, type: e.target.value })}>
              <option>customer</option>
              <option>country</option>
            </select>
          </label>
          <label>
            Value
            <input
              value={form.value}
              onChange={e => setForm({ ...form, value: e.target.value })}
              placeholder={form.type === 'country' ? 'e.g. XX' : 'customer id'}
            />
          </label>
          <label>
            Reason
            <input value={form.reason} onChange={e => setForm({ ...form, reason: e.target.value })} />
          </label>
          <button disabled={!form.value || !form.reason}>
            <Plus size={14} /> Add
          </button>
        </form>
      )}
      {error && <p className="form-msg error">{error}</p>}
      <div className="mini-list">
        {(data ?? []).map(b => (
          <div key={b.id} className="mini-row">
            <span className="chip">{b.type}</span>
            <span className="mono">{b.value}</span>
            <span className="muted-text small ellipsis" title={b.reason}>
              {b.reason}
            </span>
            {can.review(me) ? (
              <button
                className="link"
                onClick={async () => {
                  await api.removeBlocklist(b.id);
                  await refresh();
                }}
              >
                <Trash2 size={13} />
              </button>
            ) : (
              <span />
            )}
          </div>
        ))}
      </div>
    </Card>
  );
}
