import { useState } from 'react';
import { Banknote, CheckCircle2, Download, Play, Scale, TriangleAlert } from 'lucide-react';
import { api } from '../api/client';
import { can, useSession } from '../auth';
import { Card, Empty, Stat } from '../components/ui';
import { formatMoney, timeAgo, usePolling } from '../hooks';

const KIND_LABEL: Record<string, string> = {
  acquirer_clearing: 'Acquirer clearing (asset)',
  merchant_balance: 'Merchant balance (liability)',
  platform_fees: 'Platform fees (revenue)',
};

export function Ledger({ onSelect }: { onSelect: (paymentId: string) => void }) {
  const { data: accounts } = usePolling(api.ledgerAccounts, 4000);
  const { data: trial } = usePolling(api.trialBalance, 4000);
  const [account, setAccount] = useState<string | null>(null);
  const { data: entries } = usePolling(() => (account ? api.ledgerEntries(account) : Promise.resolve([])), 4000, [account]);

  const fees = (accounts ?? []).filter(a => a.kind === 'platform_fees');
  const owed = (accounts ?? []).filter(a => a.kind === 'merchant_balance');

  return (
    <>
      <section className="stats">
        <Stat title="Books" value={trial ? (trial.balanced ? 'Balanced' : 'UNBALANCED') : '—'} tone={trial?.balanced ? 'good' : 'bad'}
          hint="debits = credits, enforced by a DB trigger" />
        {fees.map(f => <Stat key={f.id} title={`Fee revenue ${f.currency}`} value={formatMoney(f.balance, f.currency)} tone="good" hint={`${f.entries} postings`} />)}
        {Object.entries(owed.reduce<Record<string, number>>((acc, a) => ({ ...acc, [a.currency]: (acc[a.currency] ?? 0) + a.balance }), {}))
          .map(([ccy, total]) => <Stat key={ccy} title={`Owed to merchants ${ccy}`} value={formatMoney(total, ccy)} hint="not yet paid out" />)}
      </section>

      <section className="grid finance-grid">
        <Card title="Accounts" aside="double-entry · click to see entries" className="wide">
          {(accounts ?? []).length === 0 ? <Empty>No postings yet. Settle a payment.</Empty> : (
            <div className="table ledger-table">
              <div className="tr th"><span>Account</span><span>Kind</span><span>Entries</span><span>Balance</span></div>
              {accounts!.map(a => (
                <button key={a.id} className={`tr row ${account === a.id ? 'selected' : ''}`} onClick={() => setAccount(a.id)}>
                  <span className="mono ellipsis">{a.id}</span>
                  <span className="muted-text">{KIND_LABEL[a.kind] ?? a.kind}</span>
                  <span>{a.entries}</span>
                  <span className={a.balance < 0 ? 'bad-text' : ''}>{formatMoney(a.balance, a.currency)}</span>
                </button>
              ))}
            </div>
          )}
        </Card>

        <Card title={account ? 'Entries' : 'Trial balance'} aside={account ?? 'per currency'} className="wide">
          {!account ? (
            <div className="table">
              <div className="tr th trial"><span>Currency</span><span>Debits</span><span>Credits</span><span>Net</span></div>
              {(trial?.currencies ?? []).map(c => (
                <div key={c.currency} className="tr trial">
                  <span>{c.currency}</span><span>{formatMoney(c.debits, c.currency)}</span><span>{formatMoney(c.credits, c.currency)}</span>
                  <span className={c.balanced ? 'good-text' : 'bad-text'}>{c.balanced ? <><CheckCircle2 size={13} /> 0.00</> : c.net}</span>
                </div>
              ))}
            </div>
          ) : (entries ?? []).length === 0 ? <Empty>Loading…</Empty> : (
            <div className="table entries-table">
              <div className="tr th"><span>When</span><span>Type</span><span>Description</span><span>Debit / credit</span></div>
              {entries!.map(e => (
                <div key={e.id} className="tr">
                  <span className="muted-text">{timeAgo(e.occurredAt)}</span>
                  <span>{e.type === 'payment_settled' || e.type === 'refund'
                    ? <button className="link" onClick={() => e.type === 'payment_settled' && onSelect(e.reference)}>{e.type}</button>
                    : <span className="chip">{e.type}</span>}</span>
                  <span className="ellipsis muted-text" title={e.description}>{e.description}</span>
                  <span className={`mono ${e.amount > 0 ? 'good-text' : 'bad-text'}`}>{e.amount > 0 ? 'Dr ' : 'Cr '}{Math.abs(e.amount).toFixed(2)}</span>
                </div>
              ))}
            </div>
          )}
          {account && <button className="link" onClick={() => setAccount(null)}>← back to trial balance</button>}
        </Card>
      </section>
    </>
  );
}

export function Payouts({ onSelect }: { onSelect: (paymentId: string) => void }) {
  const me = useSession();
  const { data, refresh } = usePolling(api.payouts, 5000);
  const [selected, setSelected] = useState<string | null>(null);
  const { data: detail } = usePolling(() => (selected ? api.payout(selected) : Promise.resolve(null)), 10000, [selected]);
  const [msg, setMsg] = useState<string | null>(null);

  const run = async () => {
    setMsg(null);
    try { const r = await api.runPayouts(); setMsg(`${r.created} payout(s) created`); await refresh(); }
    catch (e) { setMsg(e instanceof Error ? e.message : String(e)); }
  };

  return (
    <section className="grid finance-grid">
      <Card title="Payouts" className="wide" aside={
        can.admin(me) ? <button onClick={run}><Play size={13} /> Run payouts now</button> : 'scheduled batches'}>
        <p className="muted">Each payout pays a merchant its settled payments minus fees and refunds, posted to the ledger as
          <span className="mono"> Dr merchant balance / Cr clearing</span>.</p>
        {msg && <p className="form-msg ok">{msg}</p>}
        {(data ?? []).length === 0 ? <Empty>No payouts yet. Settle some payments, then run payouts.</Empty> : (
          <div className="table payouts-table">
            <div className="tr th"><span>Reference</span><span>Merchant</span><span>Items</span><span>Gross</span><span>Fees</span><span>Net</span><span>Paid</span></div>
            {data!.map(p => (
              <button key={p.id} className={`tr row ${selected === p.id ? 'selected' : ''}`} onClick={() => setSelected(p.id)}>
                <span className="mono">{p.reference}</span>
                <span className="ellipsis">{p.merchantId}</span>
                <span>{p.paymentCount}{p.refundCount ? ` − ${p.refundCount}` : ''}</span>
                <span>{formatMoney(p.grossAmount, p.currency)}</span>
                <span className="muted-text">{formatMoney(p.feeAmount, p.currency)}</span>
                <span><b>{formatMoney(p.netAmount, p.currency)}</b></span>
                <span className="muted-text">{p.paidAt ? timeAgo(p.paidAt) : '—'}</span>
              </button>
            ))}
          </div>
        )}
      </Card>

      <Card title="Payout detail" className="wide" aside={detail ? detail.payout.reference : 'select a payout'}>
        {!detail ? <Empty>Select a payout to see its line items.</Empty> : (
          <>
            <dl className="details">
              <dt>Gross</dt><dd>{formatMoney(detail.payout.grossAmount, detail.payout.currency)}</dd>
              <dt>Fees</dt><dd>− {formatMoney(detail.payout.feeAmount, detail.payout.currency)}</dd>
              <dt>Refunds</dt><dd>− {formatMoney(detail.payout.refundAmount, detail.payout.currency)}</dd>
              <dt>Net paid</dt><dd><b>{formatMoney(detail.payout.netAmount, detail.payout.currency)}</b></dd>
            </dl>
            <button onClick={() => api.downloadPayoutCsv(detail.payout.id, `${detail.payout.reference}.csv`)}><Download size={13} /> CSV report</button>
            <h3 className="section-h">Payments</h3>
            <div className="mini-list">
              {detail.payments.map(p => (
                <button key={p.id} className="mini-row row" onClick={() => onSelect(p.id)}>
                  <span className="mono small">{p.id.slice(0, 8)}…</span>
                  <span>{formatMoney(p.captured, detail.payout.currency)}</span>
                  <span className="muted-text small">fee {p.fee.toFixed(2)}</span>
                  <span className="muted-text small">{p.settlementReference}</span>
                </button>
              ))}
            </div>
            {detail.refunds.length > 0 && <>
              <h3 className="section-h">Refunds</h3>
              <div className="mini-list">
                {detail.refunds.map(r => (
                  <button key={r.id} className="mini-row row" onClick={() => onSelect(r.paymentId)}>
                    <span className="mono small">{r.id.slice(0, 8)}…</span>
                    <span className="bad-text">− {formatMoney(r.amount, detail.payout.currency)}</span>
                    <span className="muted-text small">{r.acquirerReference}</span>
                    <span className="muted-text small">{timeAgo(r.completedAt)}</span>
                  </button>
                ))}
              </div>
            </>}
          </>
        )}
      </Card>
    </section>
  );
}

const ITEM_LABEL: Record<string, string> = {
  AmountMismatch: 'Amount mismatch',
  MissingAtAcquirer: 'Missing at acquirer',
  MissingInSettleFlow: 'Missing in SettleFlow',
  MissingLedgerPosting: 'Missing ledger posting',
  LedgerImbalance: 'Ledger imbalance',
};

export function Reconciliation({ onSelect }: { onSelect: (paymentId: string) => void }) {
  const me = useSession();
  const { data: runs, refresh } = usePolling(api.reconRuns, 5000);
  const [selected, setSelected] = useState<string | null>(null);
  const current = selected ?? runs?.[0]?.id ?? null;
  const { data: detail, refresh: refreshDetail } = usePolling(() => (current ? api.reconRun(current) : Promise.resolve(null)), 5000, [current]);
  const [error, setError] = useState<string | null>(null);

  const run = async () => {
    setError(null);
    try { const r = await api.runReconciliation(); setSelected(r.id); await refresh(); }
    catch (e) { setError(e instanceof Error ? e.message : String(e)); }
  };
  const resolve = async (id: string) => {
    const note = window.prompt('How was this resolved?');
    if (!note) return;
    try { await api.resolveReconItem(id, note); await Promise.all([refreshDetail(), refresh()]); }
    catch (e) { setError(e instanceof Error ? e.message : String(e)); }
  };

  return (
    <section className="grid finance-grid">
      <Card title="Runs" className="wide" aside={can.admin(me) ? <button onClick={run}><Scale size={13} /> Reconcile last 24h</button> : 'hourly'}>
        <p className="muted">Compares every settlement and refund three ways: SettleFlow's records, the acquirer's records, and the ledger.
          It also checks that the books balance.</p>
        {error && <p className="form-msg error">{error}</p>}
        {(runs ?? []).length === 0 ? <Empty>No runs yet.</Empty> : (
          <div className="table recon-table">
            <div className="tr th"><span>Started</span><span>By</span><span>Matched</span><span>Issues</span><span>Books</span></div>
            {runs!.map(r => (
              <button key={r.id} className={`tr row ${current === r.id ? 'selected' : ''}`} onClick={() => setSelected(r.id)}>
                <span className="muted-text">{timeAgo(r.startedAt)}</span>
                <span className="mono">{r.triggeredBy}</span>
                <span className="good-text">{r.matched}</span>
                <span className={r.unresolved ? 'bad-text' : 'muted-text'}>{r.unresolved ? `${r.unresolved} open` : r.discrepancies ? 'all resolved' : 'none'}</span>
                <span>{r.ledgerBalanced ? <em className="badge settled">balanced</em> : <em className="badge declined">unbalanced</em>}</span>
              </button>
            ))}
          </div>
        )}
      </Card>

      <Card title="Discrepancies" className="wide" aside={detail ? `${detail.items.length} item(s)` : ''}>
        {!detail ? <Empty>Select a run.</Empty> : detail.items.length === 0 ? (
          <Empty><CheckCircle2 size={16} /> Everything matched: {detail.run.matched} settlements and refunds agree on all three sides.</Empty>
        ) : (
          <div className="dlq">
            {detail.items.map(i => (
              <div key={i.id} className={`dlq-item ${i.resolvedAt ? 'resolved' : ''}`}>
                <div className="dlq-main">
                  <div><TriangleAlert size={13} /> <b>{ITEM_LABEL[i.kind] ?? i.kind}</b> <span className="chip">{i.operation}</span>
                    {i.merchantId && <small className="muted-text"> {i.merchantId}</small>}</div>
                  <p className={i.resolvedAt ? 'muted-text' : 'bad-text'}>{i.details}</p>
                  {i.reference && i.operation === 'capture' && <button className="link mono" onClick={() => onSelect(i.reference!)}>payment {i.reference}</button>}
                  {i.resolvedAt && <p className="muted-text small">Resolved by {i.resolvedBy}: “{i.resolutionNote}”</p>}
                </div>
                {!i.resolvedAt && can.admin(me) && <button onClick={() => resolve(i.id)}><Banknote size={13} /> Resolve</button>}
              </div>
            ))}
          </div>
        )}
      </Card>
    </section>
  );
}
