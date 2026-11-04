import { useState } from 'react';
import { ChevronLeft, ChevronRight, Search } from 'lucide-react';
import { api } from '../api/client';
import { PaymentsTable } from '../components/PaymentsTable';
import { Card } from '../components/ui';
import { usePolling } from '../hooks';
import { ALL_STATUSES, STATUS_LABEL } from '../types';

const PAGE_SIZE = 20;

export function Payments({ onSelect, tick, fixedStatus, title }: {
  onSelect: (id: string) => void; tick: number; fixedStatus?: string; title?: string;
}) {
  const [status, setStatus] = useState(fixedStatus ?? '');
  const [q, setQ] = useState('');
  const [query, setQuery] = useState('');
  const [page, setPage] = useState(1);

  const { data, error } = usePolling(
    () => api.payments({ status, q: query, page, pageSize: PAGE_SIZE }), 2500, [status, query, page, tick]);

  const pages = data ? Math.max(1, Math.ceil(data.total / PAGE_SIZE)) : 1;

  return (
    <Card title={title ?? 'All payments'} aside={data ? `${data.total} payments` : 'Loading…'} className="wide">
      {!fixedStatus && (
        <div className="filters">
          <form className="search" onSubmit={e => { e.preventDefault(); setPage(1); setQuery(q); }}>
            <Search size={14} />
            <input placeholder="Search by payment id, customer or merchant…" value={q} onChange={e => setQ(e.target.value)} />
          </form>
          <div className="pills">
            <button className={status === '' ? 'on' : ''} onClick={() => { setStatus(''); setPage(1); }}>All</button>
            {ALL_STATUSES.map(s => (
              <button key={s} className={status === s ? 'on' : ''} onClick={() => { setStatus(s); setPage(1); }}>{STATUS_LABEL[s]}</button>
            ))}
          </div>
        </div>
      )}
      {error && <p className="form-msg error">{error}</p>}
      <PaymentsTable payments={data?.items ?? []} onSelect={onSelect} />
      {pages > 1 && (
        <div className="pager">
          <button disabled={page <= 1} onClick={() => setPage(p => p - 1)}><ChevronLeft size={14} /></button>
          <span>Page {page} of {pages}</span>
          <button disabled={page >= pages} onClick={() => setPage(p => p + 1)}><ChevronRight size={14} /></button>
        </div>
      )}
    </Card>
  );
}
