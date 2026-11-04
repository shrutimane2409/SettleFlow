import { useState } from 'react';
import { api } from '../api/client';
import { Card, Empty, Stat } from '../components/ui';
import { formatMoney, usePolling } from '../hooks';
import type { AnalyticsData } from '../types';

const RANGES = [7, 30, 90];

function DailyChart({ data }: { data: AnalyticsData['daily'] }) {
  if (data.length === 0) return <Empty>No payments in this period.</Empty>;
  const max = Math.max(1, ...data.map(d => d.payments));
  return (
    <div className="chart">
      <div className="bars stacked">
        {data.map(d => {
          const other = Math.max(0, d.payments - d.approved - d.review - d.declined);
          const h = (n: number) => `${(n / max) * 100}%`;
          return (
            <div
              key={d.day}
              className="bar"
              title={`${new Date(d.day).toLocaleDateString()}: ${d.payments} payments · ${d.approved} approved · ${d.review} review · ${d.declined} declined · ${d.chargebacks} chargebacks`}
            >
              <div className="stack-col">
                <span className="seg declined" style={{ height: h(d.declined) }} />
                <span className="seg review" style={{ height: h(d.review) }} />
                <span className="seg other" style={{ height: h(other) }} />
                <span className="seg approved" style={{ height: h(d.approved) }} />
              </div>
            </div>
          );
        })}
      </div>
      <div className="chart-legend">
        <span>{new Date(data[0].day).toLocaleDateString()}</span>
        <span>
          <i className="dot approved" /> approved <i className="dot review" /> review <i className="dot declined" /> declined
        </span>
        <span>{new Date(data[data.length - 1].day).toLocaleDateString()}</span>
      </div>
    </div>
  );
}

export function Analytics() {
  const [days, setDays] = useState(30);
  const { data } = usePolling(() => api.analytics(days), 30000, [days]);

  const totals = (data?.daily ?? []).reduce(
    (t, d) => ({
      payments: t.payments + d.payments,
      approved: t.approved + d.approved,
      volume: t.volume + d.settledVolume,
      chargebacks: t.chargebacks + d.chargebacks,
    }),
    { payments: 0, approved: 0, volume: 0, chargebacks: 0 },
  );
  const maxReason = Math.max(1, ...(data?.reasons ?? []).map(r => r.count));

  return (
    <>
      <div className="pills">
        {RANGES.map(r => (
          <button key={r} className={days === r ? 'on' : ''} onClick={() => setDays(r)}>
            Last {r} days
          </button>
        ))}
      </div>
      <section className="stats">
        <Stat title="Payments" value={totals.payments} />
        <Stat title="Settled volume" value={formatMoney(totals.volume, 'EUR')} tone="good" />
        <Stat title="Approval rate" value={totals.payments ? `${((100 * totals.approved) / totals.payments).toFixed(1)}%` : '—'} />
        <Stat title="Chargebacks" value={totals.chargebacks} tone={totals.chargebacks ? 'bad' : undefined} />
      </section>

      <section className="grid finance-grid">
        <Card title="Daily payments by outcome" className="wide" aside={`${days} days`}>
          {data ? <DailyChart data={data.daily} /> : <Empty>Loading…</Empty>}
        </Card>
        <Card title="Why payments were flagged" aside="fraud rule hits">
          {(data?.reasons ?? []).length === 0 ? (
            <Empty>No rule hits.</Empty>
          ) : (
            <div className="hbars">
              {data!.reasons.map(r => (
                <div key={r.reason} className="hbar">
                  <span className="mono small">{r.reason}</span>
                  <div>
                    <span style={{ width: `${(r.count / maxReason) * 100}%` }} />
                  </div>
                  <b>{r.count}</b>
                </div>
              ))}
            </div>
          )}
        </Card>
        <Card title="Top merchants" className="wide" aside="by settled volume">
          <div className="table an-merchants">
            <div className="tr th">
              <span>Merchant</span>
              <span>Payments</span>
              <span>Approval</span>
              <span>Settled</span>
              <span>Avg risk</span>
              <span>Refunds</span>
              <span>Chargebacks</span>
            </div>
            {(data?.merchants ?? []).map(m => (
              <div className="tr" key={m.merchantId}>
                <span className="ellipsis">{m.merchantId}</span>
                <span>{m.payments}</span>
                <span>{m.approvalRate}%</span>
                <span>{formatMoney(m.settledVolume, 'EUR')}</span>
                <span>{m.avgRisk}</span>
                <span>{m.refunds}</span>
                <span className={m.chargebacks ? 'bad-text' : ''}>{m.chargebacks}</span>
              </div>
            ))}
          </div>
        </Card>
        <Card title="Countries">
          <div className="table an-countries">
            <div className="tr th">
              <span>Country</span>
              <span>Payments</span>
              <span>Approval</span>
              <span>Avg risk</span>
            </div>
            {(data?.countries ?? []).map(c => (
              <div className="tr" key={c.country}>
                <span>{c.country}</span>
                <span>{c.payments}</span>
                <span>{c.approvalRate}%</span>
                <span>{c.avgRisk}</span>
              </div>
            ))}
          </div>
        </Card>
      </section>
    </>
  );
}
