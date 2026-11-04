import type { Payment } from '../types';
import { formatMoney, timeAgo } from '../hooks';
import { Empty, RiskMeter, StatusBadge } from './ui';

export function PaymentsTable({ payments, onSelect, compact }: { payments: Payment[]; onSelect: (id: string) => void; compact?: boolean }) {
  if (payments.length === 0) return <Empty>No payments yet. Submit one to watch it flow through the pipeline.</Empty>;

  return (
    <div className={`table ${compact ? 'compact' : ''}`}>
      <div className="tr th">
        <span>Payment</span>
        <span>Amount</span>
        {!compact && <span>Customer → Merchant</span>}
        <span>Risk</span>
        <span>Status</span>
        <span>Created</span>
      </div>
      {payments.map(p => (
        <button className="tr row" key={p.id} onClick={() => onSelect(p.id)}>
          <span className="mono">{p.id.slice(0, 8)}…</span>
          <span>
            {formatMoney(p.amount, p.currency)} <small className="muted-text">{p.country}</small>
          </span>
          {!compact && (
            <span className="ellipsis">
              {p.customerId} → {p.merchantId}
            </span>
          )}
          <span>
            <RiskMeter score={p.riskScore} />
          </span>
          <span>
            <StatusBadge status={p.status} />
          </span>
          <span className="muted-text">{timeAgo(p.createdAt)}</span>
        </button>
      ))}
    </div>
  );
}
