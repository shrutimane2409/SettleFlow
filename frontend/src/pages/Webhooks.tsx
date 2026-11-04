import { useState } from 'react';
import { RotateCcw } from 'lucide-react';
import { api } from '../api/client';
import { can, useSession } from '../auth';
import { Card, Empty } from '../components/ui';
import { timeAgo, usePolling } from '../hooks';

const STATUSES = ['', 'Pending', 'Delivered', 'Failed'];

export function Webhooks({ onSelect }: { onSelect: (id: string) => void }) {
  const me = useSession();
  const [status, setStatus] = useState('');
  const { data, refresh } = usePolling(() => api.webhookDeliveries({ status }), 3000, [status]);
  const [error, setError] = useState<string | null>(null);

  const resend = async (id: string) => {
    setError(null);
    try { await api.resendDelivery(id); await refresh(); } catch (e) { setError(e instanceof Error ? e.message : String(e)); }
  };

  return (
    <Card title="Webhook deliveries" className="wide" aside="signed with HMAC-SHA256 · retried with backoff">
      <p className="muted">
        Every merchant-facing event is POSTed to the merchant's webhook URL with a <span className="mono">SettleFlow-Signature</span> header.
        Failed deliveries are retried (10s, 30s, 2m, 10m, 30m, 1h…) and can be resent manually. Configure URLs on the Merchants page.
      </p>
      <div className="pills">
        {STATUSES.map(s => <button key={s} className={status === s ? 'on' : ''} onClick={() => setStatus(s)}>{s || 'All'}</button>)}
      </div>
      {error && <p className="form-msg error">{error}</p>}
      {(data ?? []).length === 0 ? <Empty>No deliveries yet. Settle a payment for merchant_demo to see one.</Empty> : (
        <div className="table hooks-table">
          <div className="tr th"><span>Event</span><span>Merchant</span><span>Status</span><span>Attempts</span><span>Last result</span><span>Created</span><span /></div>
          {data!.map(d => (
            <div className="tr" key={d.id}>
              <span className="mono">{d.eventType}</span>
              <span className="ellipsis">{d.merchantId}</span>
              <span><em className={`badge ${d.status === 'Delivered' ? 'settled' : d.status === 'Failed' ? 'declined' : 'pendingfraud'}`}>{d.status}</em></span>
              <span>{d.attempts}</span>
              <span className="ellipsis muted-text" title={d.lastError ?? ''}>{d.lastStatusCode ? `HTTP ${d.lastStatusCode}` : d.lastError ?? '—'}</span>
              <span className="muted-text">{timeAgo(d.createdAt)}</span>
              <span className="row-actions">
                {d.paymentId && <button className="link" onClick={() => onSelect(d.paymentId!)}>payment</button>}
                {can.admin(me) && d.status !== 'Pending' && <button onClick={() => resend(d.id)}><RotateCcw size={13} /> Resend</button>}
              </span>
            </div>
          ))}
        </div>
      )}
    </Card>
  );
}
