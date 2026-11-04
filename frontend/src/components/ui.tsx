import type { ReactNode } from 'react';
import { STATUS_LABEL, type PaymentStatus } from '../types';

export function StatusBadge({ status }: { status: PaymentStatus }) {
  return <em className={`badge ${status.toLowerCase()}`}>{STATUS_LABEL[status] ?? status}</em>;
}

export function RiskMeter({ score }: { score: number | null }) {
  if (score === null) return <span className="muted-text">—</span>;
  const level = score >= 50 ? 'high' : score >= 25 ? 'medium' : 'low';
  return (
    <span className={`risk risk-${level}`} title={`Risk score ${score}/100`}>
      <span className="risk-bar"><span style={{ width: `${Math.min(100, score)}%` }} /></span>
      {score}
    </span>
  );
}

export function Stat({ title, value, hint, tone }: { title: string; value: ReactNode; hint?: ReactNode; tone?: 'good' | 'warn' | 'bad' }) {
  return (
    <div className={`stat ${tone ?? ''}`}>
      <span>{title}</span>
      <strong>{value}</strong>
      {hint && <small>{hint}</small>}
    </div>
  );
}

export function Card({ title, aside, children, className }: { title: string; aside?: ReactNode; children: ReactNode; className?: string }) {
  return (
    <div className={`card ${className ?? ''}`}>
      <div className="cardhead">
        <h2>{title}</h2>
        {aside && <span>{aside}</span>}
      </div>
      {children}
    </div>
  );
}

export function Empty({ children }: { children: ReactNode }) {
  return <div className="empty">{children}</div>;
}

/** Tiny dependency-free bar chart of payments per minute. */
export function ThroughputChart({ data }: { data: { minute: string; count: number }[] }) {
  const max = Math.max(1, ...data.map(d => d.count));
  const total = data.reduce((s, d) => s + d.count, 0);
  return (
    <div className="chart">
      <div className="bars">
        {data.map(d => (
          <div key={d.minute} className="bar" title={`${new Date(d.minute).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}: ${d.count}`}>
            <span style={{ height: `${(d.count / max) * 100}%` }} />
          </div>
        ))}
      </div>
      <div className="chart-legend">
        <span>30 min ago</span>
        <span>{total} payments · peak {max}/min</span>
        <span>now</span>
      </div>
    </div>
  );
}
