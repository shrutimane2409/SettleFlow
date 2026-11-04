import { useState } from 'react';
import { Check, X } from 'lucide-react';
import { api } from '../api/client';
import { can, useSession } from '../auth';
import { formatMoney, usePolling } from '../hooks';
import { RiskMeter, StatusBadge } from './ui';

const EVENT_ICON: Record<string, string> = {
  Created: '●', FraudScored: '◆', ReviewApproved: '✓', ReviewRejected: '✕',
  Settled: '✓', SettlementDeclined: '✕', Notified: '✉', DeadLetterReplayed: '↻',
};

export function PaymentDrawer({ id, onClose, onChanged }: { id: string; onClose: () => void; onChanged: () => void }) {
  const { data: payment, refresh: refreshPayment } = usePolling(() => api.payment(id), 1500, [id]);
  const { data: timeline, refresh: refreshTimeline } = usePolling(() => api.timeline(id), 1500, [id]);
  const me = useSession();
  const [note, setNote] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const review = async (decision: 'APPROVE' | 'REJECT') => {
    setBusy(true);
    setError(null);
    try {
      await api.review(id, decision, note || undefined);
      setNote('');
      await Promise.all([refreshPayment(), refreshTimeline()]);
      onChanged();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="drawer-backdrop" onClick={onClose}>
      <aside className="drawer" onClick={e => e.stopPropagation()}>
        <div className="drawer-head">
          <div>
            <p className="eyebrow">PAYMENT</p>
            <h2 className="mono">{id}</h2>
          </div>
          <button onClick={onClose} aria-label="Close"><X size={16} /></button>
        </div>

        {!payment ? <p className="muted">Loading…</p> : (
          <>
            <div className="drawer-amount">
              <strong>{formatMoney(payment.amount, payment.currency)}</strong>
              <StatusBadge status={payment.status} />
            </div>

            <dl className="details">
              <dt>Customer</dt><dd>{payment.customerId}</dd>
              <dt>Merchant</dt><dd>{payment.merchantId}</dd>
              <dt>Country</dt><dd>{payment.country}</dd>
              <dt>Risk</dt><dd><RiskMeter score={payment.riskScore} /></dd>
              {payment.riskReasons.length > 0 && (<><dt>Signals</dt><dd>{payment.riskReasons.map(r => <span key={r} className="chip">{r}</span>)}</dd></>)}
              {payment.settlementReference && (<><dt>Reference</dt><dd className="mono">{payment.settlementReference}</dd></>)}
              {payment.failureReason && (<><dt>Failure</dt><dd className="bad-text">{payment.failureReason}</dd></>)}
              {payment.reviewedBy && (<><dt>Reviewed by</dt><dd>{payment.reviewedBy}{payment.reviewNote ? ` — “${payment.reviewNote}”` : ''}</dd></>)}
            </dl>

            {payment.status === 'FraudReview' && can.review(me) && (
              <div className="review-box">
                <h3>Manual review</h3>
                <p className="muted small">Reviewing as <b>{me?.user.username}</b>. Your decision is recorded in the audit log.</p>
                <label>Note<input value={note} placeholder="optional" onChange={e => setNote(e.target.value)} /></label>
                <div className="form-actions">
                  <button className="approve" disabled={busy} onClick={() => review('APPROVE')}><Check size={15} /> Approve</button>
                  <button className="reject" disabled={busy} onClick={() => review('REJECT')}><X size={15} /> Reject</button>
                </div>
                {error && <p className="form-msg error">{error}</p>}
              </div>
            )}

            <h3>Timeline</h3>
            <ol className="timeline">
              {(timeline ?? []).map((e, i) => (
                <li key={i} className={`tl-${e.type}`}>
                  <span className="tl-icon">{EVENT_ICON[e.type] ?? '•'}</span>
                  <div>
                    <b>{e.type}</b> <small className="muted-text">{e.source} · {new Date(e.occurredAt).toLocaleTimeString()}</small>
                    {e.detail && <p>{e.detail}</p>}
                  </div>
                </li>
              ))}
            </ol>
          </>
        )}
      </aside>
    </div>
  );
}
