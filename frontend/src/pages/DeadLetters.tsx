import { useState } from 'react';
import { RotateCcw } from 'lucide-react';
import { api } from '../api/client';
import { Card, Empty } from '../components/ui';
import { timeAgo, usePolling } from '../hooks';

export function DeadLetters({ onSelect }: { onSelect: (id: string) => void }) {
  const [showReplayed, setShowReplayed] = useState(false);
  const { data, refresh } = usePolling(() => api.deadLetters(showReplayed), 3000, [showReplayed]);
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const replay = async (id: string) => {
    setBusy(id);
    setError(null);
    try {
      await api.replayDeadLetter(id);
      await refresh();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(null);
    }
  };

  return (
    <Card title="Dead-letter queue" className="wide"
      aside={<label className="toggle"><input type="checkbox" checked={showReplayed} onChange={e => setShowReplayed(e.target.checked)} /> show replayed</label>}>
      <p className="muted">
        Messages a consumer could not process after all retries. Fix the cause, then replay to re-deliver the original
        event to that consumer only (others skip it thanks to the inbox).
      </p>
      {error && <p className="form-msg error">{error}</p>}
      {(data ?? []).length === 0 ? <Empty>The dead-letter queue is empty. Try the “Acquirer outage” scenario.</Empty> : (
        <div className="dlq">
          {data!.map(d => (
            <div key={d.id} className="dlq-item">
              <div className="dlq-main">
                <div><b>{d.consumer}</b> <span className="chip">{d.topic}</span> <small className="muted-text">p{d.partition}@{d.offset} · {d.attempts} attempts · {timeAgo(d.createdAt)}</small></div>
                <p className="bad-text">{d.error}</p>
                {d.key && <button className="link mono" onClick={() => onSelect(d.key!)}>payment {d.key}</button>}
              </div>
              {d.replayedAt
                ? <span className="muted-text">replayed {timeAgo(d.replayedAt)}</span>
                : <button disabled={busy === d.id} onClick={() => replay(d.id)}><RotateCcw size={14} /> Replay</button>}
            </div>
          ))}
        </div>
      )}
    </Card>
  );
}
