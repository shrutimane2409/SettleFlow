import { useState } from 'react';
import { Copy, KeyRound, Plus, Webhook } from 'lucide-react';
import { api } from '../api/client';
import { Card, Empty } from '../components/ui';
import { timeAgo, usePolling } from '../hooks';

function useAction() {
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const run = async (fn: () => Promise<unknown>) => {
    setBusy(true);
    setError(null);
    try { await fn(); } catch (e) { setError(e instanceof Error ? e.message : String(e)); } finally { setBusy(false); }
  };
  return { error, busy, run };
}

/** Shows a freshly issued secret (API key or webhook signing secret) exactly once. */
function SecretBanner({ secret, onDone }: { secret: { id: string; apiKey: string; label?: string }; onDone: () => void }) {
  const [copied, setCopied] = useState(false);
  return (
    <div className="secret">
      <b>{secret.label ?? 'API key'} for {secret.id}</b>
      <p className="muted">Copy it now. It is stored only as a hash and can't be shown again.</p>
      <div className="secret-row">
        <code className="mono">{secret.apiKey}</code>
        <button type="button" onClick={() => { navigator.clipboard.writeText(secret.apiKey); setCopied(true); }}>
          <Copy size={14} /> {copied ? 'Copied' : 'Copy'}
        </button>
        <button type="button" onClick={onDone}>Done</button>
      </div>
    </div>
  );
}

export function Merchants() {
  const { data, refresh } = usePolling(api.merchants, 10000);
  const [id, setId] = useState('');
  const [name, setName] = useState('');
  const [secret, setSecret] = useState<{ id: string; apiKey: string; label?: string } | null>(null);
  const { error, busy, run } = useAction();

  const setWebhook = (merchantId: string, current: string | null) => run(async () => {
    const url = window.prompt(`Webhook URL for ${merchantId} (empty to disable)`, current ?? 'https://');
    if (url === null) return;
    if (url.trim() === '') { await api.disableWebhook(merchantId); }
    else {
      const r = await api.configureWebhook(merchantId, url.trim());
      setSecret({ id: merchantId, apiKey: r.signingSecret, label: 'Webhook signing secret' });
    }
    await refresh();
  });

  return (
    <Card title="Merchants" aside={data ? `${data.length} merchants` : 'Loading…'} className="wide">
      <form className="inline-form" onSubmit={e => {
        e.preventDefault();
        run(async () => { setSecret(await api.createMerchant(id.trim(), name.trim())); setId(''); setName(''); await refresh(); });
      }}>
        <label>Merchant id<input value={id} placeholder="merchant_acme" onChange={e => setId(e.target.value)} /></label>
        <label>Name<input value={name} placeholder="Acme Ltd" onChange={e => setName(e.target.value)} /></label>
        <button className="primary" disabled={busy || !id || !name}><Plus size={15} /> Create merchant</button>
      </form>
      {error && <p className="form-msg error">{error}</p>}
      {secret && <SecretBanner secret={secret} onDone={() => setSecret(null)} />}

      <div className="table admin-table">
        <div className="tr th"><span>Merchant</span><span>Name</span><span>API key</span><span>Webhook</span><span>Status</span><span /></div>
        {(data ?? []).map(m => (
          <div className="tr" key={m.id}>
            <span className="mono">{m.id}</span>
            <span className="ellipsis">{m.name}</span>
            <span className="mono muted-text" title={`issued ${timeAgo(m.keyRotatedAt ?? m.createdAt)}`}>{m.apiKeyPrefix}…</span>
            <span className="ellipsis muted-text" title={m.webhookUrl ?? ''}>{m.webhookUrl ?? 'off'}</span>
            <span><em className={`badge ${m.disabled ? 'declined' : 'settled'}`}>{m.disabled ? 'Disabled' : 'Active'}</em></span>
            <span className="row-actions">
              <button disabled={busy} title="Set the webhook URL" onClick={() => setWebhook(m.id, m.webhookUrl)}><Webhook size={13} /> Webhook</button>
              <button disabled={busy} title="Issue a new key; the old one stops working immediately"
                onClick={() => run(async () => { setSecret(await api.rotateMerchantKey(m.id)); await refresh(); })}>
                <KeyRound size={13} /> Rotate
              </button>
              <button disabled={busy} onClick={() => run(async () => { await api.setMerchantDisabled(m.id, !m.disabled); await refresh(); })}>
                {m.disabled ? 'Enable' : 'Disable'}
              </button>
            </span>
          </div>
        ))}
      </div>
    </Card>
  );
}

export function Users() {
  const { data, refresh } = usePolling(api.users, 10000);
  const [form, setForm] = useState({ username: '', password: '', role: 'analyst' });
  const { error, busy, run } = useAction();

  return (
    <Card title="Users" aside={data ? `${data.length} users` : 'Loading…'} className="wide">
      <form className="inline-form" onSubmit={e => {
        e.preventDefault();
        run(async () => { await api.createUser(form.username, form.password, form.role); setForm({ ...form, username: '', password: '' }); await refresh(); });
      }}>
        <label>Username<input value={form.username} onChange={e => setForm({ ...form, username: e.target.value })} /></label>
        <label>Password<input type="password" value={form.password} placeholder="min 10 characters" onChange={e => setForm({ ...form, password: e.target.value })} /></label>
        <label>Role
          <select value={form.role} onChange={e => setForm({ ...form, role: e.target.value })}>
            <option value="viewer">viewer: read only</option>
            <option value="analyst">analyst: can review</option>
            <option value="admin">admin: everything</option>
          </select>
        </label>
        <button className="primary" disabled={busy || !form.username || !form.password}><Plus size={15} /> Add user</button>
      </form>
      {error && <p className="form-msg error">{error}</p>}

      <div className="table admin-table">
        <div className="tr th"><span>User</span><span>Role</span><span>Status</span><span>Last login</span><span>Created</span><span /></div>
        {(data ?? []).map(u => (
          <div className="tr" key={u.id}>
            <span className="mono">{u.username}</span>
            <span><span className="chip">{u.role}</span></span>
            <span><em className={`badge ${u.disabled ? 'declined' : u.locked ? 'fraudreview' : 'settled'}`}>{u.disabled ? 'Disabled' : u.locked ? 'Locked' : 'Active'}</em></span>
            <span className="muted-text">{u.lastLoginAt ? timeAgo(u.lastLoginAt) : 'never'}</span>
            <span className="muted-text">{timeAgo(u.createdAt)}</span>
            <span className="row-actions">
              <button disabled={busy} onClick={() => run(async () => { await api.setUserDisabled(u.id, !u.disabled); await refresh(); })}>
                {u.disabled ? 'Enable' : 'Disable'}
              </button>
            </span>
          </div>
        ))}
      </div>
    </Card>
  );
}

export function AuditLog({ onSelect }: { onSelect: (paymentId: string) => void }) {
  const [filter, setFilter] = useState({ actor: '', action: '' });
  const [applied, setApplied] = useState(filter);
  const { data } = usePolling(() => api.audit(applied), 5000, [applied.actor, applied.action]);

  return (
    <Card title="Audit log" aside="append-only · enforced by a database trigger" className="wide">
      <form className="inline-form" onSubmit={e => { e.preventDefault(); setApplied(filter); }}>
        <label>Actor<input value={filter.actor} placeholder="e.g. admin" onChange={e => setFilter({ ...filter, actor: e.target.value })} /></label>
        <label>Action starts with<input value={filter.action} placeholder="e.g. payment.review" onChange={e => setFilter({ ...filter, action: e.target.value })} /></label>
        <button>Filter</button>
      </form>
      {(data ?? []).length === 0 ? <Empty>No entries.</Empty> : (
        <div className="table audit-table">
          <div className="tr th"><span>When</span><span>Actor</span><span>Action</span><span>Target</span><span>Details</span><span>IP</span></div>
          {data!.map(a => (
            <div className="tr" key={a.id}>
              <span className="muted-text" title={new Date(a.occurredAt).toLocaleString()}>{timeAgo(a.occurredAt)}</span>
              <span><span className="mono">{a.actor}</span> <small className="muted-text">{a.actorType}</small></span>
              <span><span className={`chip ${a.action.includes('failed') ? 'chip-bad' : ''}`}>{a.action}</span></span>
              <span className="ellipsis">
                {a.targetType === 'payment' && a.targetId
                  ? <button className="link mono" onClick={() => onSelect(a.targetId!)}>{a.targetId.slice(0, 8)}…</button>
                  : <span className="mono">{a.targetId ?? '—'}</span>}
              </span>
              <span className="ellipsis mono muted-text" title={a.details ?? ''}>{a.details ?? ''}</span>
              <span className="mono muted-text">{a.ipAddress ?? ''}</span>
            </div>
          ))}
        </div>
      )}
    </Card>
  );
}
