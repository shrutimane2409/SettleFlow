import { useState } from 'react';
import { Ban, Check, Gavel, HandCoins, Undo2, X } from 'lucide-react';
import { api } from '../api/client';
import { can, useSession } from '../auth';
import { formatMoney, timeAgo, usePolling } from '../hooks';
import type { Payment } from '../types';
import { RiskMeter, StatusBadge } from './ui';

const EVENT_ICON: Record<string, string> = {
  Created: '●',
  FraudScored: '◆',
  ReviewApproved: '✓',
  ReviewRejected: '✕',
  Authorized: '⏸',
  CaptureRequested: '↧',
  Voided: '⊘',
  Settled: '✓',
  SettlementDeclined: '✕',
  RefundRequested: '↺',
  Refunded: '↺',
  RefundFailed: '✕',
  Notified: '✉',
  DeadLetterReplayed: '↻',
};

function useAction(onDone: () => Promise<unknown> | void) {
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const run = async (fn: () => Promise<unknown>) => {
    setBusy(true);
    setError(null);
    try {
      await fn();
      await onDone();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };
  return { error, busy, run };
}

function ReviewBox({ payment, onDone }: { payment: Payment; onDone: () => Promise<unknown> }) {
  const me = useSession();
  const [note, setNote] = useState('');
  const { error, busy, run } = useAction(onDone);
  const review = (d: 'APPROVE' | 'REJECT') =>
    run(async () => {
      await api.review(payment.id, d, note || undefined);
      setNote('');
    });

  return (
    <div className="action-box warn-box">
      <h3>Manual review</h3>
      <p className="muted small">
        Reviewing as <b>{me?.user.username}</b>. Your decision is recorded in the audit log.
      </p>
      <label>
        Note
        <input value={note} placeholder="optional" onChange={e => setNote(e.target.value)} />
      </label>
      <div className="form-actions">
        <button className="approve" disabled={busy} onClick={() => review('APPROVE')}>
          <Check size={15} /> Approve
        </button>
        <button className="reject" disabled={busy} onClick={() => review('REJECT')}>
          <X size={15} /> Reject
        </button>
      </div>
      {error && <p className="form-msg error">{error}</p>}
    </div>
  );
}

function CaptureBox({ payment, onDone }: { payment: Payment; onDone: () => Promise<unknown> }) {
  const [amount, setAmount] = useState(String(payment.amount));
  const { error, busy, run } = useAction(onDone);
  return (
    <div className="action-box info-box">
      <h3>Authorized: funds on hold</h3>
      <p className="muted small">
        Capture all or part of {formatMoney(payment.amount, payment.currency)}, or void to release the hold. Uncaptured authorizations are
        voided automatically after 7 days.
      </p>
      <label>
        Amount to capture
        <input value={amount} onChange={e => setAmount(e.target.value)} />
      </label>
      <div className="form-actions">
        <button className="approve" disabled={busy} onClick={() => run(() => api.capture(payment.id, Number(amount)))}>
          <HandCoins size={15} /> Capture
        </button>
        <button className="reject" disabled={busy} onClick={() => run(() => api.void(payment.id, 'voided from dashboard'))}>
          <Ban size={15} /> Void
        </button>
      </div>
      {error && <p className="form-msg error">{error}</p>}
    </div>
  );
}

function RefundBox({ payment, onDone }: { payment: Payment; onDone: () => Promise<unknown> }) {
  const [amount, setAmount] = useState('');
  const [reason, setReason] = useState('');
  const { error, busy, run } = useAction(onDone);
  return (
    <div className="action-box">
      <h3>Refund</h3>
      <p className="muted small">
        Refundable now: <b>{formatMoney(payment.refundableAmount, payment.currency)}</b>
      </p>
      <div className="fields two">
        <label>
          Amount
          <input value={amount} placeholder={`${payment.refundableAmount} (full)`} onChange={e => setAmount(e.target.value)} />
        </label>
        <label>
          Reason
          <input value={reason} placeholder="optional" onChange={e => setReason(e.target.value)} />
        </label>
      </div>
      <button
        disabled={busy}
        onClick={() =>
          run(async () => {
            await api.refund(payment.id, amount ? Number(amount) : undefined, reason || undefined);
            setAmount('');
            setReason('');
          })
        }
      >
        <Undo2 size={15} /> Issue refund
      </button>
      {error && <p className="form-msg error">{error}</p>}
    </div>
  );
}

function ChargebackBox({ payment, onDone }: { payment: Payment; onDone: () => Promise<unknown> }) {
  const [reason, setReason] = useState('fraud');
  const [block, setBlock] = useState(true);
  const { error, busy, run } = useAction(onDone);
  return (
    <div className="action-box">
      <h3>Record chargeback</h3>
      <p className="muted small">When the acquirer reports a dispute. Debits the merchant the disputed amount plus the dispute fee.</p>
      <div className="fields two">
        <label>
          Reason
          <select value={reason} onChange={e => setReason(e.target.value)}>
            {['fraud', 'not_received', 'not_as_described', 'duplicate', 'cancelled', 'other'].map(r => (
              <option key={r}>{r}</option>
            ))}
          </select>
        </label>
        <label className="toggle">
          <input type="checkbox" checked={block} onChange={e => setBlock(e.target.checked)} /> Blocklist customer
        </label>
      </div>
      <button className="reject" disabled={busy} onClick={() => run(() => api.chargeback(payment.id, reason, block))}>
        <Gavel size={15} /> Record chargeback
      </button>
      {error && <p className="form-msg error">{error}</p>}
    </div>
  );
}

export function PaymentDrawer({ id, onClose, onChanged }: { id: string; onClose: () => void; onChanged: () => void }) {
  const me = useSession();
  const { data: payment, refresh: refreshPayment } = usePolling(() => api.payment(id), 1500, [id]);
  const { data: timeline, refresh: refreshTimeline } = usePolling(() => api.timeline(id), 1500, [id]);
  const { data: refunds, refresh: refreshRefunds } = usePolling(() => api.refunds(id), 2500, [id]);
  const { data: deliveries } = usePolling(() => api.webhookDeliveries({ paymentId: id }), 3000, [id]);

  const refreshAll = async () => {
    await Promise.all([refreshPayment(), refreshTimeline(), refreshRefunds()]);
    onChanged();
  };

  const canWrite = can.createPayments(me);

  return (
    <div className="drawer-backdrop" onClick={onClose}>
      <aside className="drawer" onClick={e => e.stopPropagation()}>
        <div className="drawer-head">
          <div>
            <p className="eyebrow">PAYMENT</p>
            <h2 className="mono">{id}</h2>
          </div>
          <button onClick={onClose} aria-label="Close">
            <X size={16} />
          </button>
        </div>

        {!payment ? (
          <p className="muted">Loading…</p>
        ) : (
          <>
            <div className="drawer-amount">
              <strong>{formatMoney(payment.amount, payment.currency)}</strong>
              <StatusBadge status={payment.status} />
            </div>

            <dl className="details">
              <dt>Customer</dt>
              <dd>{payment.customerId}</dd>
              <dt>Merchant</dt>
              <dd>{payment.merchantId}</dd>
              <dt>Country</dt>
              <dd>{payment.country}</dd>
              <dt>Capture</dt>
              <dd>{payment.captureMode}</dd>
              {payment.capturedAmount != null && (
                <>
                  <dt>Captured</dt>
                  <dd>{formatMoney(payment.capturedAmount, payment.currency)}</dd>
                </>
              )}
              {payment.riskRuleSetVersion != null && (
                <>
                  <dt>Rules</dt>
                  <dd>
                    v{payment.riskRuleSetVersion}
                    {payment.riskModelScore != null ? ` · model ${payment.riskModelScore.toFixed(2)}` : ''}
                  </dd>
                </>
              )}
              {payment.chargebackAmount > 0 && (
                <>
                  <dt>Charged back</dt>
                  <dd className="bad-text">{formatMoney(payment.chargebackAmount, payment.currency)}</dd>
                </>
              )}
              {payment.feeAmount != null && (
                <>
                  <dt>Fee</dt>
                  <dd>{formatMoney(payment.feeAmount, payment.currency)}</dd>
                </>
              )}
              {payment.payoutId && (
                <>
                  <dt>Paid out</dt>
                  <dd className="mono small">{payment.payoutId.slice(0, 8)}…</dd>
                </>
              )}
              {payment.refundedAmount > 0 && (
                <>
                  <dt>Refunded</dt>
                  <dd>{formatMoney(payment.refundedAmount, payment.currency)}</dd>
                </>
              )}
              <dt>Risk</dt>
              <dd>
                <RiskMeter score={payment.riskScore} />
              </dd>
              {payment.riskReasons.length > 0 && (
                <>
                  <dt>Signals</dt>
                  <dd>
                    {payment.riskReasons.map(r => (
                      <span key={r} className="chip">
                        {r}
                      </span>
                    ))}
                  </dd>
                </>
              )}
              {payment.settlementReference && (
                <>
                  <dt>Reference</dt>
                  <dd className="mono">{payment.settlementReference}</dd>
                </>
              )}
              {payment.failureReason && (
                <>
                  <dt>Failure</dt>
                  <dd className="bad-text">{payment.failureReason}</dd>
                </>
              )}
              {payment.reviewedBy && (
                <>
                  <dt>Reviewed by</dt>
                  <dd>
                    {payment.reviewedBy}
                    {payment.reviewNote ? ` — “${payment.reviewNote}”` : ''}
                  </dd>
                </>
              )}
            </dl>

            {payment.status === 'FraudReview' && can.review(me) && <ReviewBox payment={payment} onDone={refreshAll} />}
            {payment.status === 'Authorized' && canWrite && <CaptureBox payment={payment} onDone={refreshAll} />}
            {payment.refundableAmount > 0 && canWrite && <RefundBox payment={payment} onDone={refreshAll} />}
            {payment.refundableAmount > 0 && can.review(me) && <ChargebackBox payment={payment} onDone={refreshAll} />}

            {(refunds ?? []).length > 0 && (
              <>
                <h3>Refunds</h3>
                <div className="mini-list">
                  {refunds!.map(r => (
                    <div key={r.id} className="mini-row">
                      <span>{formatMoney(r.amount, r.currency)}</span>
                      <em className={`badge ${r.status === 'Succeeded' ? 'settled' : r.status === 'Failed' ? 'declined' : 'pendingfraud'}`}>
                        {r.status}
                      </em>
                      <span className="muted-text small ellipsis">{r.reason ?? r.failureReason ?? r.acquirerReference ?? ''}</span>
                      <span className="muted-text small">
                        {r.createdBy} · {timeAgo(r.createdAt)}
                      </span>
                    </div>
                  ))}
                </div>
              </>
            )}

            {(deliveries ?? []).length > 0 && (
              <>
                <h3>Webhooks</h3>
                <div className="mini-list">
                  {deliveries!.map(d => (
                    <div key={d.id} className="mini-row">
                      <span className="mono small">{d.eventType}</span>
                      <em className={`badge ${d.status === 'Delivered' ? 'settled' : d.status === 'Failed' ? 'declined' : 'pendingfraud'}`}>
                        {d.status}
                      </em>
                      <span className="muted-text small">
                        {d.attempts} attempt{d.attempts === 1 ? '' : 's'}
                        {d.lastStatusCode ? ` · HTTP ${d.lastStatusCode}` : ''}
                      </span>
                      <span className="muted-text small">{timeAgo(d.createdAt)}</span>
                    </div>
                  ))}
                </div>
              </>
            )}

            <h3>Timeline</h3>
            <ol className="timeline">
              {(timeline ?? []).map((e, i) => (
                <li key={i} className={`tl-${e.type}`}>
                  <span className="tl-icon">{EVENT_ICON[e.type] ?? '•'}</span>
                  <div>
                    <b>{e.type}</b>{' '}
                    <small className="muted-text">
                      {e.source} · {new Date(e.occurredAt).toLocaleTimeString()}
                    </small>
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
