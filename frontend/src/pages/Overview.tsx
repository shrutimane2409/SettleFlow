import { api } from '../api/client';
import { PaymentForm } from '../components/PaymentForm';
import { PaymentsTable } from '../components/PaymentsTable';
import { Card, Empty, Stat, ThroughputChart } from '../components/ui';
import { formatMoney, timeAgo, usePolling } from '../hooks';

const NOTE_TONE: Record<string, string> = { SETTLED: 'good', FAILED: 'bad', REVIEW_REQUIRED: 'warn' };

export function Overview({ onSelect, tick, onCreated }: { onSelect: (id: string) => void; tick: number; onCreated: () => void }) {
  const { data: stats } = usePolling(api.stats, 2000, [tick]);
  const { data: recent } = usePolling(() => api.payments({ pageSize: 10 }), 2000, [tick]);
  const { data: notes } = usePolling(() => api.notifications(8), 2500, [tick]);

  const s = stats?.byStatus;
  const inFlight = (s?.PendingFraud ?? 0) + (s?.FraudApproved ?? 0);
  const volume = stats?.settledVolume[0];

  return (
    <>
      <section className="stats">
        <Stat title="Total payments" value={stats?.total ?? '—'} hint={`${inFlight} in flight`} />
        <Stat title="Settled" value={s?.Settled ?? '—'} tone="good"
          hint={volume ? `${formatMoney(volume.volume, volume.currency)}${stats!.settledVolume.length > 1 ? ' +more' : ''}` : 'no volume yet'} />
        <Stat title="Success rate" value={stats?.successRate != null ? `${stats.successRate}%` : '—'} hint={`avg risk ${stats?.averageRiskScore ?? '—'}`} />
        <Stat title="Awaiting review" value={stats?.reviewQueue ?? '—'} tone={stats?.reviewQueue ? 'warn' : undefined} hint="manual decisions" />
        <Stat title="Declined / failed" value={(s?.Declined ?? 0) + (s?.SettlementFailed ?? 0)} tone="bad"
          hint={`${s?.Declined ?? 0} fraud · ${s?.SettlementFailed ?? 0} acquirer`} />
        <Stat title="Pipeline" value={stats ? `${stats.pipeline.outboxPending}` : '—'}
          tone={stats?.pipeline.deadLetters ? 'bad' : 'good'}
          hint={stats ? `outbox lag ${stats.pipeline.outboxLagSeconds}s · ${stats.pipeline.deadLetters} DLQ` : ''} />
      </section>

      <section className="grid">
        <Card title="Create payment" aside="API → Outbox → Kafka">
          <PaymentForm onCreated={onCreated} />
        </Card>

        <div className="stack">
          <Card title="Throughput" aside="payments / minute">
            {stats ? <ThroughputChart data={stats.throughput} /> : <Empty>Loading…</Empty>}
          </Card>
          <Card title="Recent payments" aside="live · click a row for its timeline" className="wide">
            <PaymentsTable payments={recent?.items ?? []} onSelect={onSelect} compact />
          </Card>
        </div>

        <Card title="Notifications" aside="delivered by notification-worker" className="notes">
          {(notes ?? []).length === 0 ? <Empty>Nothing delivered yet.</Empty> : (
            <ul className="feed">
              {notes!.map(n => (
                <li key={n.id} onClick={() => onSelect(n.paymentId)}>
                  <i className={NOTE_TONE[n.type] ?? ''} />
                  <div>
                    <b>{n.type.replace('_', ' ')}</b>
                    <p>{n.message}</p>
                    <small className="muted-text">{timeAgo(n.createdAt)}</small>
                  </div>
                </li>
              ))}
            </ul>
          )}
        </Card>
      </section>
    </>
  );
}
